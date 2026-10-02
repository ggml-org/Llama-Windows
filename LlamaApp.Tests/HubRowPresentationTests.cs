using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the Hub search-result row's presentation helpers
/// (<see cref="HubRowPresentation"/>): the compact counter's unit boundaries
/// and the relative-age buckets, plus the metadata line's assembly with the
/// freshness part and its omission rule.
/// </summary>
public class HubRowPresentationTests
{
    // ---- FormatCount: decimal k/M/B, one fraction below 10, whole above ----

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1K")]
    [InlineData(1023, "1K")]
    [InlineData(1234, "1.2K")]
    [InlineData(9999, "10K")] // 9.999 rounds to 10 — whole above the threshold
    [InlineData(10_000, "10K")]
    [InlineData(12_345, "12K")] // ≥10K: whole numbers, no fraction digit
    [InlineData(1_499, "1.5K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(12_345_678, "12.3M")]
    [InlineData(1_000_000_000, "1B")]
    [InlineData(1_500_000_000, "1.5B")]
    public void FormatCount_Uses_Decimal_Units_Like_The_Catalog(long n, string expected)
    {
        Assert.Equal(expected, HubRowPresentation.FormatCount(n));
    }

    // ---- FormatAge: coarse relative buckets ----

    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]                     // same instant
    [InlineData(-30, "just now")]                   // future (clock skew) clamps
    [InlineData(30, "just now")]                    // seconds — below the minute bucket
    [InlineData(60, "1 minute ago")]
    [InlineData(5 * 60, "5 minutes ago")]
    [InlineData(59 * 60, "59 minutes ago")]
    [InlineData(60 * 60, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(23 * 3600, "23 hours ago")]
    [InlineData(24 * 3600, "1 day ago")]
    [InlineData(3L * 86400, "3 days ago")]
    [InlineData(6L * 86400, "6 days ago")]
    [InlineData(7L * 86400, "1 week ago")]
    [InlineData(13L * 86400, "1 week ago")]         // 13/7 = 1 (integer)
    [InlineData(14L * 86400, "2 weeks ago")]
    [InlineData(29L * 86400, "4 weeks ago")]
    [InlineData(30L * 86400, "1 month ago")]
    [InlineData(45L * 86400, "1 month ago")]        // 45/30 = 1
    [InlineData(95L * 86400, "3 months ago")]
    [InlineData(364L * 86400, "12 months ago")]
    [InlineData(365L * 86400, "1 year ago")]
    [InlineData(800L * 86400, "2 years ago")]
    public void FormatAge_Buckets_Round_Down_With_Sane_Plurals(long ageSeconds, string expected)
    {
        var lastModified = Now - TimeSpan.FromSeconds(ageSeconds);

        Assert.Equal(expected, HubRowPresentation.FormatAge(lastModified, Now));
    }

    [Fact]
    public void FormatAge_Missing_Value_Yields_Null_So_The_Part_Drops_Out()
    {
        Assert.Null(HubRowPresentation.FormatAge(null, Now));
    }

    // ---- MetaText assembly (through the view-model) ----

    [Fact]
    public void MetaText_Joins_Author_Counts_And_Freshness()
    {
        // MetaText reads the real clock (relative time), so anchor the
        // timestamp to it — 60 days lands mid-bucket, immune to execution
        // delay.
        var vm = new HubModelItemViewModel
        {
            RepoId = "ggml-org/gemma-3-4b-it-GGUF",
            Author = "ggml-org",
            Downloads = 1_234_567,
            Likes = 312,
            LastModified = DateTimeOffset.Now - TimeSpan.FromDays(60),
        };

        Assert.Equal("ggml-org · 1.2M downloads · 312 likes · updated 2 months ago", vm.MetaText);
    }

    [Fact]
    public void MetaText_Omits_Unknown_Parts_Individually()
    {
        var vm = new HubModelItemViewModel { RepoId = "org/repo", Downloads = 999, Likes = 0 };

        Assert.Equal("999 downloads", vm.MetaText); // no author, no likes, no freshness

        var fresh = new HubModelItemViewModel
        {
            RepoId = "org/repo",
            LastModified = DateTimeOffset.Now - TimeSpan.FromSeconds(30),
        };
        Assert.Equal("updated just now", fresh.MetaText);
    }

    [Fact]
    public void MetaText_Without_Anything_Known_Is_Empty()
    {
        var vm = new HubModelItemViewModel { RepoId = "org/repo" };

        Assert.Equal("", vm.MetaText);
    }
}
