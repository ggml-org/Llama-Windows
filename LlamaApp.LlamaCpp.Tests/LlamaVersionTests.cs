using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for <see cref="LlamaVersion"/>: the installed-build parser shared by
/// every version comparison (both <c>--version</c> line formats llama.cpp has
/// printed), and the pinned-runtime floor
/// (<see cref="LlamaVersion.IsPinnedOrNewer"/>) that decides which binaries the
/// app may launch and which must be repaired through the installer.
/// </summary>
public sealed class LlamaVersionTests
{
    // ---- ParseInstalledBuild: llama --version output ----

    [Theory]
    [InlineData("llama-server (llama) b9553 (abcdef)", 9553u)]
    [InlineData("version: 6726 (abcdef)", 6726u)]
    [InlineData("llama.cpp build: 1234", 1234u)]
    // Since b10398: semantic version first, build number in the parenthetical.
    [InlineData("version: 0.7.0-dev (build 11941, commit 19ff817fb)", 11941u)]
    public void ParseInstalledBuild_ParsesKnownFormats(string line, uint expected)
    {
        Assert.Equal(expected, LlamaVersion.ParseInstalledBuild(line));
    }

    [Theory]
    [InlineData("llama-server version unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseInstalledBuild_RejectsUnrecognizableOutput(string? line)
    {
        Assert.Null(LlamaVersion.ParseInstalledBuild(line));
    }

    [Fact]
    public void ParseInstalledBuild_TreatsTheDevBuildSentinelAsUnparsable()
    {
        // llama.cpp dev builds print version 0xFFFFFFFF — not a release, so
        // there is nothing to compare against and the check skips.
        Assert.Null(LlamaVersion.ParseInstalledBuild("version: 4294967295 (release)"));
    }

    [Fact]
    public void ParseInstalledBuild_ExplicitBuildLabelWinsOverBToken()
    {
        // A line carrying both forms resolves to the explicit "build:" value.
        Assert.Equal(1234u, LlamaVersion.ParseInstalledBuild("b9553 (build: 1234)"));
    }

    // ---- Semver build labels ----

    [Theory]
    [InlineData("version: 0.7.0-dev (build 12765, commit 19ff817fb)", 12765u)]
    [InlineData("version: 0.8.0 (build 20000, commit abcdef123)", 20000u)]
    // Without a build label the "version:" label wins the parse — a bare
    // semver yields its major component (0 here), never a guessed build.
    [InlineData("version: 0.8.0", 0u)]
    public void ParseInstalledBuild_ReadsTheSemverBuildLabel(string line, uint expected)
    {
        Assert.Equal(expected, LlamaVersion.ParseInstalledBuild(line));
    }

    // ---- IsPinnedOrNewer: the pinned runtime floor (b12765) ----

    [Theory]
    [InlineData("llama-server (llama) b12764 (abcdef)")]
    [InlineData("version: 0.7.0-dev (build 12764, commit 19ff817fb)")]
    [InlineData("version: 6726 (abcdef)")]
    public void IsPinnedOrNewer_RejectsBuildsBelowThePin(string versionLine)
    {
        Assert.False(LlamaVersion.IsPinnedOrNewer(versionLine));
    }

    [Theory]
    [InlineData("llama-server (llama) b12765 (abcdef)")]
    [InlineData("version: 0.7.0-dev (build 12765, commit 19ff817fb)")]
    [InlineData("version: 12765 (abcdef)")]
    [InlineData("llama-server (llama) b12766")]
    [InlineData("llama-server (llama) b12800 (abcdef)")]
    public void IsPinnedOrNewer_AcceptsThePinAndNewer(string versionLine)
    {
        // The pin is a floor, not a match: the pinned build and anything newer
        // pass — a working newer engine is never downgraded.
        Assert.True(LlamaVersion.IsPinnedOrNewer(versionLine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some unrecognizable banner")]
    [InlineData("version: 4294967295 (release)")]
    public void IsPinnedOrNewer_RejectsUnreadableVersions(string? versionLine)
    {
        // An unreadable version proves nothing — rejected like an old build.
        Assert.False(LlamaVersion.IsPinnedOrNewer(versionLine));
    }
}
