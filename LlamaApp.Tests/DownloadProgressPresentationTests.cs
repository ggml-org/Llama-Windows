using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="DownloadProgressPresentation"/> — the download
/// detail line ("3.2 GB of 12.1 GB · 45 MB/s · ~4 min left"), its byte/ETA
/// formatting, and the smoothed speed estimate.
/// </summary>
public class DownloadProgressPresentationTests
{
    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2_500, "3 KB")]
    [InlineData(45_000_000, "45 MB")]
    [InlineData(2_526_080_992, "2.5 GB")]
    [InlineData(12_109_566_560, "12.1 GB")]
    [InlineData(1_500_000_000_000, "1.5 TB")]
    public void FormatBytes_Uses_Decimal_Units_Like_The_Catalog(double bytes, string expected)
    {
        Assert.Equal(expected, DownloadProgressPresentation.FormatBytes(bytes));
    }

    [Fact]
    public void FormatDetail_Without_Speed_Shows_Only_Bytes()
    {
        // A stalled stream (speed 0) must not show a bogus "0 MB/s · ∞ left".
        Assert.Equal(
            "3.2 GB of 12.1 GB",
            DownloadProgressPresentation.FormatDetail(3_200_000_000, 12_100_000_000, 0));
    }

    [Fact]
    public void FormatDetail_With_Speed_Appends_Rate_And_Eta()
    {
        // 8.9 GB remaining at 45 MB/s ≈ 198 s ≈ ~3 min.
        var text = DownloadProgressPresentation.FormatDetail(
            3_200_000_000, 12_100_000_000, 45_000_000);

        Assert.StartsWith("3.2 GB of 12.1 GB · 45 MB/s · ", text);
        Assert.EndsWith("left", text);
    }

    [Fact]
    public void FormatDetail_Omits_Eta_When_Nothing_Remains()
    {
        var text = DownloadProgressPresentation.FormatDetail(
            12_100_000_000, 12_100_000_000, 45_000_000);

        Assert.Equal("12.1 GB of 12.1 GB · 45 MB/s", text);
    }

    [Fact]
    public void FormatPausedDetail_Shows_Frozen_Bytes_Without_Speed_Or_Eta()
    {
        // A paused download's speed/ETA are meaningless — only the frozen
        // byte counts are shown, with a "Paused" marker.
        Assert.Equal(
            "Paused · 3.2 GB of 12.1 GB",
            DownloadProgressPresentation.FormatPausedDetail(3_200_000_000, 12_100_000_000));
    }

    [Fact]
    public void FormatPausedDetail_Formats_Small_Counts()
    {
        Assert.Equal(
            "Paused · 45 MB of 500 MB",
            DownloadProgressPresentation.FormatPausedDetail(45_000_000, 500_000_000));
    }

    [Theory]
    [InlineData(5, "~5 s left")]
    [InlineData(59, "~59 s left")]
    [InlineData(61, "~1 min left")]
    [InlineData(240, "~4 min left")]
    [InlineData(5_300, "~88 min left")]
    [InlineData(5_500, "~2 h left")]
    public void FormatEta_Is_Deliberately_Coarse(double seconds, string expected)
    {
        Assert.Equal(expected, DownloadProgressPresentation.FormatEta(seconds));
    }

    [Fact]
    public void SmoothSpeed_Seeds_From_The_First_Sample()
    {
        Assert.Equal(100.0, DownloadProgressPresentation.SmoothSpeed(0, 100));
    }

    [Fact]
    public void SmoothSpeed_EMA_Damps_Spikes()
    {
        // 0.7/0.3 blend: a spike to 1000 from a steady 100 lands at 370, not 1000.
        Assert.Equal(370.0, DownloadProgressPresentation.SmoothSpeed(100, 1000), precision: 6);
    }
}
