using System.Text.RegularExpressions;

namespace LlamaApp.Llama;

/// <summary>
/// The pinned llama.cpp runtime contract for this app release: which build the
/// official <c>install.ps1</c> delivers, and the version-line parsing every
/// pin comparison shares. The app installs exactly <see cref="LLAMA_VERSION"/>
/// and only launches binaries at that build or newer — a newer user-managed
/// engine is fine, an older or unreadable one is not.
/// </summary>
public static class LlamaVersion
{
    /// <summary>The exact <c>LLAMA_VERSION</c> value install.ps1 selects from
    /// its install bucket. Const fields are implicitly static in C#.</summary>
    public const string LLAMA_VERSION = "b12650";

    /// <summary>Numeric build floor derived once from <see cref="LLAMA_VERSION"/>.</summary>
    private static readonly uint PinnedBuild = ParseInstalledBuild(LLAMA_VERSION)
        ?? throw new InvalidOperationException($"Invalid llama.cpp runtime pin: {LLAMA_VERSION}");

    /// <summary>
    /// Extracts the build number from the llama binary's <c>--version</c>
    /// output — the first line llama.cpp prints, e.g.
    /// <c>llama-server (llama) b9553 (abcdef)</c>, <c>version: 6726 (abcdef)</c>,
    /// or — since llama.cpp adopted semantic versions —
    /// <c>version: 0.7.0-dev (build 11941, commit 19ff817fb)</c>.
    /// Null when no recognizable build number is present.
    /// </summary>
    internal static uint? ParseInstalledBuild(string? versionLine)
    {
        if (string.IsNullOrWhiteSpace(versionLine)) return null;

        // "build: 9553" / "(build 11941, ..." / "version: 6726" — the explicit
        // labels first, then a bare b-number token ("b9553") anywhere in the
        // line. The build label must win over "version:": in the semver format,
        // "version: 0.7.0-dev" would otherwise parse as build 0, making every
        // installed runtime look outdated.
        foreach (var pattern in new[] { @"\bbuild:?\s*(\d+)", @"version:\s*(\d+)", @"\bb(\d{2,7})\b" })
        {
            var match = Regex.Match(versionLine, pattern, RegexOptions.IgnoreCase);
            // uint: dev builds print the 0xFFFFFFFF sentinel, which overflows
            // int. That sentinel means "not a release build" — unparsable for
            // comparison purposes, so the pin check treats it as unsupported.
            if (match.Success && uint.TryParse(match.Groups[1].Value, out var build) && build != uint.MaxValue)
                return build;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="versionLine"/> proves an installed build of at
    /// least <see cref="LLAMA_VERSION"/> — the pin is a floor, never a match:
    /// newer builds pass. Null or unreadable is false: an engine whose version
    /// cannot be read could be anything, including a build that dies on the
    /// app's launch flags with no diagnosable failure.
    /// </summary>
    internal static bool IsPinnedOrNewer(string? versionLine)
        => ParseInstalledBuild(versionLine) is { } build && build >= PinnedBuild;
}
