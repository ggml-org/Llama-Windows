using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="DiskSpace"/> — the download preflight probe.
/// The contract that matters: a failed probe never blocks a download.
/// </summary>
public class DiskSpaceTests
{
    [Fact]
    public void Probe_Of_An_Existing_Path_Succeeds()
    {
        var ok = DiskSpace.TryGetAvailableFreeBytes(
            Path.GetTempPath(), out var freeBytes);

        Assert.True(ok);
        Assert.True(freeBytes > 0);
    }

    [Fact]
    public void Probe_Of_A_NotYetExisting_Path_Resolves_The_Root()
    {
        // The cache folder may not exist yet on first run — the probe must
        // still find the drive.
        var path = Path.Combine(Path.GetTempPath(), "llamaapp-test-" + Guid.NewGuid().ToString("N"));
        var ok = DiskSpace.TryGetAvailableFreeBytes(path, out var freeBytes);

        Assert.True(ok);
        Assert.True(freeBytes > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Probe_Of_A_Missing_Path_Is_Unknown_Not_An_Error(string? path)
    {
        Assert.False(DiskSpace.TryGetAvailableFreeBytes(path, out var freeBytes));
        Assert.Equal(0, freeBytes);
    }

    [Fact]
    public void HasEnoughSpace_Allows_A_Tiny_Download()
    {
        Assert.True(DiskSpace.HasEnoughSpace(Path.GetTempPath(), 1, out _));
    }

    [Fact]
    public void HasEnoughSpace_Blocks_An_Impossible_Download()
    {
        // No drive has ulong.MaxValue bytes free.
        Assert.False(DiskSpace.HasEnoughSpace(Path.GetTempPath(), ulong.MaxValue, out _));
    }

    [Fact]
    public void HasEnoughSpace_Never_Blocks_When_The_Probe_Fails()
    {
        // Contract: an unreadable path means "unknown", and "unknown" allows —
        // a failed probe must never block a download.
        Assert.True(DiskSpace.HasEnoughSpace(null, ulong.MaxValue, out var freeBytes));
        Assert.Equal(0, freeBytes);
    }
}
