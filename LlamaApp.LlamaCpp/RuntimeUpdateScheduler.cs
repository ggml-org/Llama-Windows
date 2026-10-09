using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// Keeps the app-managed llama.cpp runtime current: about once a week, check
/// whether a newer release exists on GitHub and, when it does, install it via
/// the official <c>install.ps1</c> — the same machinery the app already uses
/// for the first install (<see cref="LlamaManager.InstallAsync"/>), so there
/// is exactly one install path and the user's own (external) installation is
/// never touched.
///
/// <para><b>Scheduling.</b> The app is an MSIX-packaged tray app that already
/// runs at login, so a Windows Task Scheduler task would add identity and
/// duplicate-run complexity for no coverage gain: an in-app check persisted
/// across restarts covers the same ground. The last check is stamped in
/// <see cref="AppData.Root"/> (<c>.runtime-update-check</c>, UTC ISO 8601);
/// at startup the check runs when the stamp is a week old or older, and an
/// hourly <see cref="PeriodicTimer"/> loop keeps watching for the week
/// elapsing during long sessions.</para>
///
/// <para><b>When installing is safe.</b> <c>install.ps1</c> overwrites
/// <c>llama.exe</c>, which Windows locks while the server runs — so an install
/// only happens at the one point the binary is not in use: at startup, before
/// <see cref="LlamaManager.EnsureLlamaOrDownloadAsync"/> launches the server
/// (a successful install then means the server comes up on the new version).
/// During a session the loop defers whenever the server is up and retries the
/// next hour; nothing is ever restarted or interrupted.</para>
///
/// <para><b>Visibility.</b> The weekly pass is silent, but not invisible:
/// <see cref="CheckNowAsync"/> backs a Settings "Check for updates" button
/// and returns what happened (<see cref="CheckOutcome"/>), and a successful
/// install raises <see cref="RuntimeUpdated"/> so the app can toast it.</para>
///
/// <para><b>Failure handling.</b> Only a confirmed outcome marks the weekly
/// check done: up to date, installed, install attempted-and-failed, or a
/// deterministic "can't compare" (unparsable installed version). A failed
/// version fetch is transient — it leaves the stamp untouched so the next
/// hourly tick retries. Everything is logged to <c>%LOCALAPPDATA%\Llama\logs</c>;
/// the scheduler must never fault the app.</para>
///
/// <para><b>Version comparison.</b> llama.cpp releases are tagged
/// <c>b&lt;build&gt;</c> (e.g. <c>b6726</c>) and build numbers are monotonic,
/// so "newer" is a plain integer comparison of the installed build (parsed
/// from the binary's <c>--version</c> output) against the latest release tag.
/// An unparsable release tag skips the check — guessing would risk
/// downgrades.</para>
/// </summary>
public sealed class RuntimeUpdateScheduler
{
    /// <summary>The result of a runtime check — what the UI should say about it.</summary>
    public enum CheckOutcome
    {
        /// <summary>The weekly interval hasn't elapsed (scheduled checks only).</summary>
        NotDue,
        /// <summary>The binary is the user's own (PATH) installation — never managed here.</summary>
        SkippedNotManaged,
        /// <summary>An install would run, but the server is up — deferred (retried when idle).</summary>
        DeferredServerBusy,
        /// <summary>The latest release couldn't be determined (network/GitHub/tag).</summary>
        FetchFailed,
        /// <summary>The installed version has no comparable build number.</summary>
        SkippedUnparsableVersion,
        /// <summary>Checked: the installed runtime is the latest release.</summary>
        UpToDate,
        /// <summary>A newer release was found and installed.</summary>
        Installed,
        /// <summary>A newer release was found but the install failed (see the log).</summary>
        InstallFailed,
    }

    /// <summary>How often the runtime is allowed to check for a new release.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>How often the scheduler wakes up to see whether a check is due.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    /// <summary>Budget for the startup version check, so it never delays the
    /// server launch meaningfully. On timeout the stamp stays untouched and
    /// the hourly loop retries — the install just happens later.</summary>
    private static readonly TimeSpan StartupCheckBudget = TimeSpan.FromSeconds(10);

    private readonly LlamaManager _manager;
    private readonly LastCheckStore _store;

    /// <summary>Serializes checks — the hourly tick, the startup pass, and a
    /// manual Check Now click must never race an install against each other.</summary>
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>Raised (on a background thread) after a successful runtime
    /// install, with the new build number — lets the app toast the event.</summary>
    public event Action<uint>? RuntimeUpdated;

    public RuntimeUpdateScheduler(LlamaManager manager, LastCheckStore? store = null)
    {
        _manager = manager;
        _store = store ?? new LastCheckStore();
    }

    /// <summary>When the last confirmed check ran (from the persisted stamp),
    /// or null when never — surfaced in Settings.</summary>
    public DateTimeOffset? LastCheckUtc => _store.Load();

    /// <summary>
    /// Runs a check now, bypassing the weekly gate — the Settings "Check for
    /// updates" button. Keeps every safety gate (external installs are never
    /// managed; installs never run under a live server) and stamps the same
    /// way a scheduled check does. Network failures leave the stamp untouched
    /// so the scheduled cadence still catches up later.
    /// </summary>
    public async Task<CheckOutcome> CheckNowAsync()
        => await RunCheckAsync(force: true, fetchBudget: null);

    /// <summary>
    /// Runs the startup check (bounded — a due check may install, delaying the
    /// server launch on that one day), then brings the server up via
    /// <paramref name="ensureServer"/>, then starts the hourly watch loop.
    /// Fire-and-forget from <c>App.OnLaunched</c>; never throws.
    /// </summary>
    public Task StartAsync(Func<Task<bool>> ensureServer)
    {
        return Task.Run(async () =>
        {
            // The check runs first (its install needs the idle window) but
            // must never prevent the server from coming up: an unexpected
            // failure is logged and the ensure still runs.
            try
            {
                await CheckDueAsync(bounded: true);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "runtime update startup check failed");
            }
            try
            {
                await ensureServer();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "runtime update startup ensure failed");
            }

            // The server is up — now just watch for the next week elapsing.
            // Installs stay deferred while it runs; a due check that can't
            // install simply retries on the next tick.
            using var timer = new PeriodicTimer(Tick);
            while (true)
            {
                try
                {
                    if (!await timer.WaitForNextTickAsync()) break;
                    await CheckDueAsync(bounded: false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log.Warn(ex, "runtime update tick failed");
                }
            }
        });
    }

    /// <summary>Runs the weekly check when its interval has elapsed.</summary>
    private async Task CheckDueAsync(bool bounded)
        => await RunCheckAsync(force: false, fetchBudget: bounded ? StartupCheckBudget : null);

    /// <summary>
    /// One check, shared by the scheduled path (<paramref name="force"/> is
    /// false) and Check Now (true). Stamping rules: only a confirmed outcome
    /// (not managed, unparsable, up to date, install attempted) marks the
    /// weekly check done; transient failures (fetch) and deferrals (server
    /// busy) leave the stamp so the next tick retries.
    /// </summary>
    private async Task<CheckOutcome> RunCheckAsync(bool force, TimeSpan? fetchBudget)
    {
        await _checkGate.WaitAsync();
        try
        {
            var manager = _manager;
            if (!force && !IsDue(_store.Load(), DateTimeOffset.UtcNow, Interval))
                return CheckOutcome.NotDue;

        // Never manage an installation the user brought themselves, and never
        // install while a server transition or another install is in flight.
        // A non-managed origin is a stable situation worth stamping — polling
        // GitHub hourly for a binary we will never touch buys nothing.
            // Never manage an installation the user brought themselves, and
            // never install while a server transition or another install is in
            // flight. A non-managed origin is a stable situation worth
            // stamping — polling GitHub hourly for a binary we will never
            // touch buys nothing.
            if (manager.CurrentOrigin != LlamaManager.Origin.Managed)
            {
                Log.Info($"runtime update skipped: installation is {manager.CurrentOrigin}, not app-managed");
                _store.Save(DateTimeOffset.UtcNow);
                return CheckOutcome.SkippedNotManaged;
            }

            // The overwrite needs the binary idle; skip without stamping so
            // the check retries when the server is down (or at the next app
            // start, which runs before the server launches).
            if (!IsServerSafe(manager.ServerStatus) || manager.State == LlamaManager.InstallState.Installing)
            {
                Log.Debug("runtime update deferred: llama server is not idle");
                return CheckOutcome.DeferredServerBusy;
            }

            CancellationToken cancel = CancellationToken.None;
            CancellationTokenSource? boundedCts = null;
            if (fetchBudget is { } budget)
            {
                boundedCts = new CancellationTokenSource(budget);
                cancel = boundedCts.Token;
            }

            uint? latest;
            try
            {
                latest = await FetchLatestBuildAsync(cancel);
            }
            finally { boundedCts?.Dispose(); }

            // Transient failure (network, GitHub, unparsable tag) — the next
            // tick retries, so this week's check stays "due".
            if (latest is null)
            {
                Log.Warn("runtime update check failed: could not determine the latest llama.cpp release");
                return CheckOutcome.FetchFailed;
            }

            var installed = ParseInstalledBuild(manager.Version);
            if (installed is null)
            {
                // Deterministic (the binary prints what it prints) — retrying
                // hourly won't change the answer, so stamp the check as done.
                Log.Warn($"runtime update skipped: installed version '{manager.Version}' has no recognizable build number");
                _store.Save(DateTimeOffset.UtcNow);
                return CheckOutcome.SkippedUnparsableVersion;
            }

            if (latest.Value <= installed.Value)
            {
                Log.Info($"llama.cpp runtime is up to date (installed b{installed}, latest b{latest})");
                _store.Save(DateTimeOffset.UtcNow);
                return CheckOutcome.UpToDate;
            }

            // Re-check after the fetch: the server may have come up in
            // between (e.g. a sibling tool or a state change during the call).
            if (!IsServerSafe(manager.ServerStatus) || manager.State == LlamaManager.InstallState.Installing)
            {
                Log.Debug("runtime update deferred: llama server became busy during the check");
                return CheckOutcome.DeferredServerBusy;
            }

            Log.Info($"llama.cpp runtime update available: installed b{installed}, latest b{latest} — installing");
            try
            {
                // Unattended: require a pinned script hash. While
                // InstallScriptIntegrity.PinnedSha256 is null the install is
                // refused (logged by InstallAsync) rather than executing an
                // unverified remote script on a weekly timer.
                var installedOk = await manager.InstallAsync(
                    CancellationToken.None, allowUnpinnedInstall: false);
                if (installedOk)
                    RuntimeUpdated?.Invoke(latest.Value);
                return installedOk ? CheckOutcome.Installed : CheckOutcome.InstallFailed;
            }
            finally
            {
                _store.Save(DateTimeOffset.UtcNow);
            }
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>
    /// Fetches the latest llama.cpp release from GitHub and returns its build
    /// number. Null on any failure — the check simply stays due.
    /// </summary>
    private static async Task<uint?> FetchLatestBuildAsync(CancellationToken cancel)
    {
        try
        {
            // Deliberately NOT a shared client: an internet call, so the
            // system proxy is welcome (same reasoning as the installer).
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Llama/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await client.GetAsync(LatestReleaseUrl, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"llama.cpp release check got HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancel);
            var build = ParseLatestReleaseBuild(json);
            if (build is null)
                Log.Warn("llama.cpp release response carried no recognizable b<build> tag; skipping");
            return build;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException
            or TaskCanceledException)
        {
            // A failed check is an expected answer, not an error — but log it
            // so a stale runtime is diagnosable.
            Log.Warn(ex, "llama.cpp release check failed");
            return null;
        }
    }

    /// <summary>GitHub releases endpoint for the latest llama.cpp release.</summary>
    private static readonly Uri LatestReleaseUrl =
        new("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");

    /// <summary>
    /// Parses the <c>releases/latest</c> JSON body into the release build
    /// number. Returns null on malformed JSON or a tag that isn't
    /// <c>b&lt;build&gt;</c> — the check stays due and is retried later.
    /// Pure and internal so the JSON binding (the field is the snake_case
    /// <c>tag_name</c>) is unit-tested without a network round-trip.
    /// </summary>
    internal static uint? ParseLatestReleaseBuild(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var release = JsonSerializer.Deserialize<LatestReleaseDto>(json);
        return ParseReleaseBuild(release?.TagName);
    }

    /// <summary>
    /// True when a weekly check should run now: never checked, or the last
    /// check is at least <paramref name="interval"/> in the past.
    /// </summary>
    internal static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
        => lastCheckUtc is null || nowUtc - lastCheckUtc.Value >= interval;

    /// <summary>
    /// True when the binary is not in use and an install can run: the server
    /// is down (stopped, or failed to start — and not mid-launch). The caller
    /// additionally requires <see cref="LlamaManager.InstallState"/> to not be
    /// mid-install.
    /// </summary>
    internal static bool IsServerSafe(LlamaManager.ServerState server)
        => server is LlamaManager.ServerState.Stopped or LlamaManager.ServerState.Failed;

    /// <summary>
    /// Extracts the build number from the llama binary's <c>--version</c>
    /// output — the first line llama.cpp prints, e.g.
    /// <c>llama-server (llama) b9553 (abcdef)</c> or <c>version: 6726 (abcdef)</c>.
    /// Null when no recognizable build number is present.
    /// </summary>
    internal static uint? ParseInstalledBuild(string? versionLine)
    {
        if (string.IsNullOrWhiteSpace(versionLine)) return null;

        // "build: 9553" / "version: 6726" — the explicit labels first, then a
        // bare b-number token ("b9553") anywhere in the line.
        foreach (var pattern in new[] { @"build:\s*(\d+)", @"version:\s*(\d+)", @"\bb(\d{2,7})\b" })
        {
            var match = Regex.Match(versionLine, pattern, RegexOptions.IgnoreCase);
            // uint: dev builds print the 0xFFFFFFFF sentinel, which overflows
            // int. That sentinel means "not a release build" — unparsable for
            // comparison purposes, so the check safely skips.
            if (match.Success && uint.TryParse(match.Groups[1].Value, out var build) && build != uint.MaxValue)
                return build;
        }
        return null;
    }

    /// <summary>
    /// Parses a llama.cpp release tag — strictly <c>b&lt;build&gt;</c>
    /// (e.g. <c>b6726</c>). Anything else (versions, decorated or
    /// release-candidate tags) yields null: only tags this app understands are
    /// comparable, and skipping is always safe.
    /// </summary>
    internal static uint? ParseReleaseBuild(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        if (t.Length < 2 || t[0] != 'b') return null;
        foreach (var c in t[1..])
            if (c is < '0' or > '9') return null;
        return uint.Parse(t[1..]);
    }

    /// <summary>releases/latest response — only the tag matters here.</summary>
    internal sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    }
}

/// <summary>
/// Persists the timestamp of the last completed runtime update check as a
/// single UTC ISO 8601 line under <see cref="AppData.Root"/>, so the weekly
/// cadence survives app restarts. Best-effort in both directions: a missing
/// or unreadable file means "never checked", a failed write is logged and
/// skipped — the worst case is a check that runs more often than weekly.
/// </summary>
public sealed class LastCheckStore
{
    private readonly string _path;

    public LastCheckStore(string? path = null)
        => _path = path ?? Path.Combine(AppData.Root, ".runtime-update-check");

    /// <summary>The last completed check, or null when never / unreadable.</summary>
    public DateTimeOffset? Load()
    {
        try
        {
            var text = File.ReadAllText(_path).Trim();
            return DateTimeOffset.Parse(text, styles: System.Globalization.DateTimeStyles.RoundtripKind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.FormatException or ArgumentException)
        {
            // A missing file is the normal first-run case — only a file that
            // exists but can't be read/parsed is worth a log line.
            if (File.Exists(_path))
                Log.Warn(ex, "runtime update check stamp is unreadable; treating as never checked");
            return null;
        }
    }

    /// <summary>Stamps the last completed check (UTC). Best-effort.</summary>
    public void Save(DateTimeOffset whenUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, whenUtc.ToUniversalTime().ToString("o"));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "could not persist the runtime update check stamp");
        }
    }
}
