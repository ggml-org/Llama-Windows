using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaApp.Common;

/// <summary>
/// A newer application release found on GitHub: the release tag as
/// published (e.g. "v0.11.0") and the URL of its release page.
/// </summary>
public sealed record AppUpdate(string Tag, Uri ReleasePageUrl);

/// <summary>
/// Checks whether a newer Llama release exists on GitHub by querying the
/// GitHub releases API for the latest non-prerelease, non-draft release and
/// comparing its tag against the running app version. The result is cached
/// in memory for a while so the tray flyout (whose window is rebuilt on each
/// open) doesn't hammer the API — GitHub rate-limits anonymous requests.
/// Every failure (network, JSON, unparsable tag, …) yields null and logs a
/// warning: the update banner is a best-effort decoration and must never
/// fault the app, but silent failures made an invisible banner impossible
/// to diagnose — %LOCALAPPDATA%\Llama\logs now says why.
/// </summary>
public static class UpdateChecker
{
    /// <summary>GitHub releases endpoint for the latest stable release.</summary>
    private static readonly Uri LatestReleaseUrl =
        new("https://api.github.com/repos/ggml-org/llama-windows/releases/latest");

    /// <summary>How long a successful check result is reused.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly object CacheLock = new();
    private static AppUpdate? _cachedUpdate;
    private static bool _hasCachedResult;
    private static DateTime _cachedAtUtc;

    /// <summary>
    /// Returns an <see cref="AppUpdate"/> when the latest GitHub release is
    /// newer than <paramref name="current"/>, otherwise null (up to date,
    /// or the check failed).
    ///
    /// WHY `async` + `await` (not a bare `return`): the short-lived
    /// <see cref="HttpClient"/> is scoped by <c>using</c>. A bare
    /// <c>return GetLatestUpdateAsync(client, …)</c> would return the in-flight
    /// Task and immediately exit the using block — disposing the client while
    /// the request is still running, which faults the fetch and hides the
    /// banner. <c>await</c> (like the sibling <c>HubClient.WhoAmI</c>) holds
    /// the client alive until the request completes; only non-async methods
    /// can dispose here safely, and this one isn't.
    /// </summary>
    public static async Task<AppUpdate?> GetLatestUpdateAsync(Version current, CancellationToken cancel = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Llama/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return await GetLatestUpdateAsync(client, current, cancel);
    }

    /// <summary>
    /// The cache-aware core: serves fresh cached results, otherwise fetches
    /// and evaluates the latest release. Split from the public overload so
    /// tests can drive the HTTP path with a mock handler.
    /// </summary>
    internal static async Task<AppUpdate?> GetLatestUpdateAsync(
        HttpClient client, Version current, CancellationToken cancel = default)
    {
        lock (CacheLock)
        {
            if (_hasCachedResult && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cachedUpdate;
        }

        AppUpdate? update;
        try
        {
            using var resp = await client.GetAsync(LatestReleaseUrl, cancel);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cancel);
            var release = JsonSerializer.Deserialize<LatestReleaseDto>(json);
            update = Evaluate(release, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or TaskCanceledException)
        {
            // A failed check is an expected answer, not an error — but log it
            // so a missing banner is diagnosable. Failures must not be cached
            // either (the network may simply be down right now).
            Log.Warn(ex, "app update check failed; banner stays hidden");
            return null;
        }

        lock (CacheLock)
        {
            _cachedUpdate = update;
            _hasCachedResult = true;
            _cachedAtUtc = DateTime.UtcNow;
        }

        Log.Info(update is { } u
            ? $"app update available: {u.Tag} (running {current}) → {u.ReleasePageUrl}"
            : $"app is up to date (running {current})");

        return update;
    }

    /// <summary>Test hook: clears the in-memory result cache.</summary>
    internal static void ResetCache()
    {
        lock (CacheLock)
        {
            _cachedUpdate = null;
            _hasCachedResult = false;
            _cachedAtUtc = default;
        }
    }

    /// <summary>
    /// Maps a parsed release to an <see cref="AppUpdate"/> — null when the
    /// payload is incomplete or its tag isn't newer than the running version.
    /// </summary>
    private static AppUpdate? Evaluate(LatestReleaseDto? release, Version current)
    {
        if (release is null) return null;
        // /releases/latest already excludes these, but trust nothing.
        if (release.Prerelease || release.Draft) return null;
        if (string.IsNullOrWhiteSpace(release.TagName)) return null;
        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var pageUrl))
        {
            Log.Warn($"release {release.TagName} has no usable html_url ({release.HtmlUrl}); banner stays hidden");
            return null;
        }

        var latest = ParseTagVersion(release.TagName);
        if (latest is null)
        {
            Log.Warn($"release tag '{release.TagName}' is not a plain numeric version; banner stays hidden");
            return null;
        }
        if (!IsNewer(latest, current)) return null;

        return new AppUpdate(release.TagName.Trim(), pageUrl);
    }

    /// <summary>
    /// Parses a release tag like "v0.11.0" (leading v optional) into a
    /// <see cref="Version"/>. Prerelease/decorated tags ("v0.11.0-beta",
    /// "release-2") intentionally yield null — only plain numeric versions
    /// are comparable, and /releases/latest already excludes prereleases.
    /// </summary>
    internal static Version? ParseTagVersion(string tag)
    {
        var trimmed = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var version) && version.Major >= 0 ? version : null;
    }

    /// <summary>
    /// True when <paramref name="latest"/> is strictly newer than
    /// <paramref name="current"/>. Undefined components (tag "0.11.0" vs
    /// assembly "0.10.0.0") count as zero, so 4-part assembly versions and
    /// 3-part release tags compare naturally.
    /// </summary>
    internal static bool IsNewer(Version latest, Version current)
    {
        if (latest.Major != current.Major) return latest.Major > current.Major;
        if (latest.Minor != current.Minor) return latest.Minor > current.Minor;

        var latestBuild = latest.Build < 0 ? 0 : latest.Build;
        var currentBuild = current.Build < 0 ? 0 : current.Build;
        if (latestBuild != currentBuild) return latestBuild > currentBuild;

        var latestRevision = latest.Revision < 0 ? 0 : latest.Revision;
        var currentRevision = current.Revision < 0 ? 0 : current.Revision;
        return latestRevision > currentRevision;
    }

    /// <summary>
    /// releases/latest response DTO — only the fields the check needs; the
    /// rest of the (large) payload (assets, body, author, …) is ignored by
    /// the deserializer.
    /// </summary>
    internal sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
    }
}
