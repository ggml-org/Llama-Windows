using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="DownloadProgressPresentation"/> — the download
/// detail line ("45 MB/s · ~4 min left" — byte counts live in the progress
/// ring), its byte/ETA formatting, and the smoothed speed estimate.
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
    public void FormatDetail_Without_Speed_Is_Empty()
    {
        // A stalled stream (speed 0) shows nothing — never a bogus
        // "0 MB/s · ∞ left", and the byte counts live in the progress ring.
        Assert.Equal(
            string.Empty,
            DownloadProgressPresentation.FormatDetail(3_200_000_000, 12_100_000_000, 0));
    }

    [Fact]
    public void FormatDetail_With_Speed_Shows_Rate_And_Eta()
    {
        // 8.9 GB remaining at 45 MB/s ≈ 198 s ≈ ~3 min.
        var text = DownloadProgressPresentation.FormatDetail(
            3_200_000_000, 12_100_000_000, 45_000_000);

        Assert.StartsWith("45 MB/s · ", text);
        Assert.EndsWith("left", text);
    }

    [Fact]
    public void FormatDetail_Omits_Eta_When_Nothing_Remains()
    {
        var text = DownloadProgressPresentation.FormatDetail(
            12_100_000_000, 12_100_000_000, 45_000_000);

        Assert.Equal("45 MB/s", text);
    }

    [Fact]
    public void FormatPausedDetail_Is_Just_The_Paused_Marker()
    {
        // A paused download's byte counts and speed/ETA are meaningless —
        // the progress ring shows the frozen position.
        Assert.Equal("Paused", DownloadProgressPresentation.FormatPausedDetail());
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

    [Fact]
    public void Duplicate_Per_File_Bytes_Do_Not_Reset_The_Speed_Sample_Clock()
    {
        var speed = new DownloadSpeedEstimator();

        Assert.Equal(0, speed.Observe(1_000, nowMs: 1_000));
        Assert.Equal(0, speed.Observe(1_000, nowMs: 2_000)); // sidecar-only event

        // Two seconds elapsed since the last REAL byte increase. If the
        // duplicate advanced the clock this would incorrectly read 100 B/s.
        Assert.Equal(50, speed.Observe(1_100, nowMs: 3_000), precision: 6);
    }

    [Fact]
    public void Regressive_Aggregate_Does_Not_Reset_Speed_Or_Create_A_Spike()
    {
        var speed = new DownloadSpeedEstimator();
        speed.Observe(1_000, nowMs: 1_000);
        speed.Observe(2_000, nowMs: 2_000);
        var before = speed.BytesPerSecond;

        Assert.Equal(before, speed.Observe(0, nowMs: 2_100));
        Assert.Equal(before, speed.Observe(100, nowMs: 2_200));

        // The next increase is measured from the last accepted 2,000-byte
        // sample, not from the regressive 100-byte event.
        Assert.Equal(1_000, speed.Observe(3_000, nowMs: 3_000), precision: 6);
    }

    [Fact]
    public void A_New_Attempt_Gets_A_Fresh_Speed_Estimate()
    {
        var firstAttempt = new DownloadSpeedEstimator();
        firstAttempt.Observe(1_000, 1_000);
        firstAttempt.Observe(2_000, 2_000);
        Assert.True(firstAttempt.BytesPerSecond > 0);

        var retry = new DownloadSpeedEstimator();
        Assert.Equal(0, retry.Observe(0, 3_000));
        Assert.Equal(0, retry.Observe(100, 4_000));
    }
}
