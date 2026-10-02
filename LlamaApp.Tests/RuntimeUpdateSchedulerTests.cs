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

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llama-scheduler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
}
