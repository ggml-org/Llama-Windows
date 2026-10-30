using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="RuntimeUpdateScheduler"/>'s pure decision logic:
/// the weekly due check, build-number parsing from both sides of the
/// comparison (installed <c>--version</c> output and GitHub release tags),
/// the server-safety guard, and the last-check stamp store.
/// </summary>
public class RuntimeUpdateSchedulerTests
{
    // ---- IsDue: the weekly cadence ----

    [Fact]
    public void IsDue_WhenNeverChecked()
    {
        Assert.True(RuntimeUpdateScheduler.IsDue(null, Now, RuntimeUpdateScheduler.Interval));
    }

    [Fact]
    public void IsDue_NotWithinTheWeek()
    {
        Assert.False(RuntimeUpdateScheduler.IsDue(Now - TimeSpan.FromHours(1), Now, RuntimeUpdateScheduler.Interval));
        // 6 days 23 hours — one hour short of the week.
        Assert.False(RuntimeUpdateScheduler.IsDue(
            Now - RuntimeUpdateScheduler.Interval + TimeSpan.FromHours(1), Now, RuntimeUpdateScheduler.Interval));
    }

    [Fact]
    public void IsDue_AtAndPastTheWeekBoundary()
    {
        // Exactly 7 days counts as due (the check runs at most once a week;
        // equality is the natural boundary).
        Assert.True(RuntimeUpdateScheduler.IsDue(Now - RuntimeUpdateScheduler.Interval, Now, RuntimeUpdateScheduler.Interval));
        Assert.True(RuntimeUpdateScheduler.IsDue(
            Now - RuntimeUpdateScheduler.Interval - TimeSpan.FromDays(30), Now, RuntimeUpdateScheduler.Interval));
    }

    // ---- ParseReleaseBuild: GitHub tags (strict b<build>) ----

    [Theory]
    [InlineData("b6726", 6726u)]
    [InlineData(" b6726 ", 6726u)]
    [InlineData("b11852\r\n", 11852u)]  // the install channel's plain-text latest marker
    [InlineData("b1", 1u)]
    public void ParseReleaseBuild_ParsesPlainBuildTags(string tag, uint expected)
    {
        Assert.Equal(expected, RuntimeUpdateScheduler.ParseReleaseBuild(tag));
    }

    [Theory]
    [InlineData("b6726-rc1")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3")]
    [InlineData("b")]
    [InlineData("babc")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseReleaseBuild_RejectsUnrecognizableTags(string? tag)
    {
        // A skipped check is always safe; guessing could downgrade the runtime.
        Assert.Null(RuntimeUpdateScheduler.ParseReleaseBuild(tag));
    }

    // ---- ParseInstalledBuild: llama --version output ----

    [Theory]
    [InlineData("llama-server (llama) b9553 (abcdef)", 9553u)]
    [InlineData("version: 6726 (abcdef)", 6726u)]
    [InlineData("llama.cpp build: 1234", 1234u)]
    // Since b10398: semantic version first, build number in the parenthetical.
    [InlineData("version: 0.7.0-dev (build 11941, commit 19ff817fb)", 11941u)]
    public void ParseInstalledBuild_ParsesKnownFormats(string line, uint expected)
    {
        Assert.Equal(expected, RuntimeUpdateScheduler.ParseInstalledBuild(line));
    }

    [Theory]
    [InlineData("llama-server version unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseInstalledBuild_RejectsUnrecognizableOutput(string? line)
    {
        Assert.Null(RuntimeUpdateScheduler.ParseInstalledBuild(line));
    }

    [Fact]
    public void ParseInstalledBuild_TreatsTheDevBuildSentinelAsUnparsable()
    {
        // llama.cpp dev builds print version 0xFFFFFFFF — not a release, so
        // there is nothing to compare against and the check skips.
        Assert.Null(RuntimeUpdateScheduler.ParseInstalledBuild("version: 4294967295 (release)"));
    }

    [Fact]
    public void ParseInstalledBuild_ExplicitBuildLabelWinsOverBToken()
    {
        // A line carrying both forms resolves to the explicit "build:" value.
        Assert.Equal(1234u, RuntimeUpdateScheduler.ParseInstalledBuild("b9553 (build: 1234)"));
    }

    // ---- ClassifyInstallOutcome: an install that changes nothing is not an update ----

    [Fact]
    public void ClassifyInstallOutcome_FailedInstall_IsAFailure()
    {
        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.InstallFailed,
            RuntimeUpdateScheduler.ClassifyInstallOutcome(false, 11852u, "version: 11852 (abcdef)"));
    }

    [Fact]
    public void ClassifyInstallOutcome_WhenTheBuildChanged_IsAnUpdate()
    {
        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.Installed,
            RuntimeUpdateScheduler.ClassifyInstallOutcome(true, 11852u, "version: 11860 (abcdef)"));
    }

    [Fact]
    public void ClassifyInstallOutcome_WhenTheBuildDidNotChange_IsUpToDate_NotAPhantomUpdate()
    {
        // The channel marker can lag the comparison source (or move between
        // check and install): a "successful" install that delivers the very
        // same build must never claim a version that never appears — that is
        // the endless "it updates at the next launch" loop (issue #133
        // follow-up).
        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.UpToDate,
            RuntimeUpdateScheduler.ClassifyInstallOutcome(true, 11852u, "version: 11852 (abcdef)"));
    }

    [Fact]
    public void ClassifyInstallOutcome_UnreadablePostInstallVersion_KeepsTheBenefitOfTheDoubt()
    {
        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.Installed,
            RuntimeUpdateScheduler.ClassifyInstallOutcome(true, 11852u, null));
        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.Installed,
            RuntimeUpdateScheduler.ClassifyInstallOutcome(true, null, "version: 11860 (abcdef)"));
    }

    // ---- IsServerSafe: installs only while the binary is idle ----

    [Theory]
    [InlineData(LlamaManager.ServerState.Stopped, true)]
    [InlineData(LlamaManager.ServerState.Failed, true)]
    [InlineData(LlamaManager.ServerState.Starting, false)]
    [InlineData(LlamaManager.ServerState.Running, false)]
    public void IsServerSafe_OnlyWhenTheServerIsDown(LlamaManager.ServerState state, bool expected)
    {
        Assert.Equal(expected, RuntimeUpdateScheduler.IsServerSafe(state));
    }

    // ---- LastCheckStore: weekly cadence survives restarts ----

    [Fact]
    public void Store_RoundtripsTheLastCheck()
    {
        var store = new LastCheckStore(Path.Combine(TempDir(), "stamp"));
        var now = DateTimeOffset.UtcNow;

        Assert.Null(store.Load()); // never checked

        store.Save(now);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        // Persisted as UTC with roundtrip precision — compare within a
        // second to stay insensitive to sub-tick truncation.
        Assert.True(Math.Abs((loaded.Value - now.ToUniversalTime()).TotalSeconds) < 1);
    }

    [Fact]
    public void Store_TreatsACorruptStampAsNeverChecked()
    {
        var path = Path.Combine(TempDir(), "corrupt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a timestamp");

        var store = new LastCheckStore(path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Store_LoadsAMissingFileAsNeverChecked_WithoutLoggingNoise()
    {
        var store = new LastCheckStore(Path.Combine(TempDir(), "missing", "stamp"));
        Assert.Null(store.Load());
    }

    // ---- LastCheckStore pending-install marker: a deferred manual check's
    // "it installs at the next launch" promise survives restarts ----

    [Fact]
    public void Store_PendingMarker_SetLoadClear_RoundTrip()
    {
        var path = Path.Combine(TempDir(), "stamp");
        var store = new LastCheckStore(path);

        Assert.False(store.IsPendingInstall()); // nothing deferred

        store.SetPendingInstall();
        Assert.True(store.IsPendingInstall());
        // Persisted, not in-memory: a fresh store (the next app launch) sees
        // the pending intent.
        Assert.True(new LastCheckStore(path).IsPendingInstall());

        store.ClearPendingInstall();
        Assert.False(store.IsPendingInstall());
        Assert.False(new LastCheckStore(path).IsPendingInstall());
    }

    [Fact]
    public void Store_PendingMarker_Is_Isolated_Per_Stamp_Path()
    {
        // Two stamp files in the SAME directory must not share one pending
        // marker — the marker derives from the stamp's own path.
        var dir = TempDir();
        var a = new LastCheckStore(Path.Combine(dir, "stamp-a"));
        var b = new LastCheckStore(Path.Combine(dir, "stamp-b"));

        a.SetPendingInstall();

        Assert.True(a.IsPendingInstall());
        Assert.False(b.IsPendingInstall());
    }

    // ---- CheckNowAsync: a busy binary defers — and a manual check keeps
    // its "installs at the next launch" promise across restarts ----
    //
    // RunCheckAsync records the pending intent at BOTH DeferredServerBusy
    // return sites when the check is manual (force) — this exercises the
    // pre-fetch site; the post-fetch site runs the same two lines. StartAsync
    // honors the marker on the next launch, before ensureServer(), while the
    // server is still Stopped — that honor step is the other half of the
    // promise (kept code-reviewed; driving StartAsync end-to-end would need
    // seams for its fire-and-forget timer loop).

    [Fact]
    public async Task CheckNowAsync_WhenTheServerIsRunning_DefersAndLeavesAPendingInstall()
    {
        var path = Path.Combine(TempDir(), "stamp");
        var store = new LastCheckStore(path);
        var scheduler = new RuntimeUpdateScheduler(CreateBusyManager(), store);

        var outcome = await scheduler.CheckNowAsync();

        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.DeferredServerBusy, outcome);
        Assert.True(store.IsPendingInstall());
        // A fresh store — i.e. the next launch — sees the pending intent.
        Assert.True(new LastCheckStore(path).IsPendingInstall());
    }

    [Fact]
    public async Task CheckNowAsync_WhenOriginIsUnknown_KeepsPendingAndDoesNotStamp()
    {
        // Before EnsureLlamaOrDownloadAsync resolves the binary, CurrentOrigin
        // is Unknown — the state the startup honor step met before it learned
        // to resolve first. Unknown is transient, NOT "not managed": the
        // pending intent must survive and the weekly stamp stay untouched.
        var path = Path.Combine(TempDir(), "stamp");
        var store = new LastCheckStore(path);
        var manager = (LlamaManager)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(LlamaManager));
        SetStateProperty(manager, nameof(LlamaManager.CurrentOrigin), LlamaManager.Origin.Unknown);
        SetStateProperty(manager, nameof(LlamaManager.ServerStatus), LlamaManager.ServerState.Stopped);
        store.SetPendingInstall();
        var scheduler = new RuntimeUpdateScheduler(manager, store);

        var outcome = await scheduler.CheckNowAsync();

        Assert.Equal(RuntimeUpdateScheduler.CheckOutcome.DeferredServerBusy, outcome);
        Assert.True(store.IsPendingInstall()); // intent survives
        Assert.Null(store.Load());             // and no stamp was written
    }

    /// <summary>
    /// A <see cref="LlamaManager"/> that reads as "app-managed install, llama
    /// server running" — the situation the Settings "Check for updates"
    /// button meets. LlamaManager is a singleton whose constructor spins up a
    /// background status supervisor (which would promptly re-derive any faked
    /// status over HTTP), so the test builds an uninitialized instance and
    /// writes the observable state through the property setters — no test
    /// seam in production code, and the deferred path touches nothing the
    /// constructor would have set up.
    /// </summary>
    private static LlamaManager CreateBusyManager()
    {
        var manager = (LlamaManager)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(LlamaManager));
        SetStateProperty(manager, nameof(LlamaManager.CurrentOrigin), LlamaManager.Origin.Managed);
        SetStateProperty(manager, nameof(LlamaManager.ServerStatus), LlamaManager.ServerState.Running);
        return manager;
    }

    private static void SetStateProperty(LlamaManager manager, string name, object value)
        // The state properties have private setters — reflection invokes
        // those just fine.
        => typeof(LlamaManager).GetProperty(name)!.SetValue(manager, value);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llama-scheduler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
}
