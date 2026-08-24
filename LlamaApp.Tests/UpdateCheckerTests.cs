using System.Net;
using System.Text;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="UpdateChecker"/>: tag parsing, version
/// comparison, the releases/latest JSON → <see cref="AppUpdate"/> mapping,
/// failure handling, and the in-memory result cache.
/// </summary>
public class UpdateCheckerTests
{
    private const string ReleaseUrl = "https://github.com/ggml-org/llama-windows/releases/tag/v0.11.0";

    public UpdateCheckerTests()
    {
        // The cache is static and shared across tests.
        UpdateChecker.ResetCache();
    }

    private static string ReleaseJson(string tag, string htmlUrl = ReleaseUrl, bool prerelease = false, bool draft = false)
        => $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "{{htmlUrl}}",
              "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
              "draft": {{draft.ToString().ToLowerInvariant()}}
            }
            """;

    private static HttpClient ClientReturning(string json, HttpStatusCode status = HttpStatusCode.OK, Action<int>? onCall = null)
    {
        var calls = 0;
        return new HttpClient(new StubHandler(_ =>
        {
            onCall?.Invoke(Interlocked.Increment(ref calls));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }));
    }

    [Theory]
    [InlineData("v0.11.0", 0, 11, 0)]
    [InlineData("V0.11.0", 0, 11, 0)]
    [InlineData("0.11.0", 0, 11, 0)]
    [InlineData(" v0.11.0 ", 0, 11, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    public void ParseTagVersion_ParsesPlainNumericTags(string tag, int major, int minor, int build)
    {
        var version = UpdateChecker.ParseTagVersion(tag);

        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Theory]
    [InlineData("v0.11.0-beta")]
    [InlineData("release-2")]
    [InlineData("")]
    [InlineData("latest")]
    public void ParseTagVersion_RejectsDecoratedTags(string tag)
    {
        Assert.Null(UpdateChecker.ParseTagVersion(tag));
    }

    [Theory]
    [InlineData("0.11.0", "0.10.0.0", true)]   // 3-part tag vs 4-part assembly
    [InlineData("0.10.1", "0.10.0.0", true)]
    [InlineData("1.0.0", "0.10.0.0", true)]
    [InlineData("0.10.0", "0.10.0.0", false)]  // equal
    [InlineData("0.10.0.0", "0.10.0", false)]  // equal the other way
    [InlineData("0.9.0", "0.10.0.0", false)]   // older
    [InlineData("0.10.0.1", "0.10.0.0", true)] // revision bump
    [InlineData("0.10.0.0", "0.10.0.1", false)]
    public void IsNewer_ComparesWithUndefinedComponentsAsZero(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(Version.Parse(latest), Version.Parse(current)));
    }

    [Fact]
    public async Task ReturnsUpdate_WhenReleaseIsNewer()
    {
        var client = ClientReturning(ReleaseJson("v0.11.0"));

        var update = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));

        Assert.NotNull(update);
        Assert.Equal("v0.11.0", update.Tag);
        Assert.Equal(new Uri(ReleaseUrl), update.ReleasePageUrl);
    }

    [Theory]
    [InlineData("v0.10.0")]   // same as current
    [InlineData("v0.9.0")]    // older
    public async Task ReturnsNull_WhenUpToDate(string tag)
    {
        var client = ClientReturning(ReleaseJson(tag));

        var update = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));

        Assert.Null(update);
    }

    [Theory]
    [InlineData(true, false)]  // prerelease
    [InlineData(false, true)]  // draft
    public async Task ReturnsNull_ForPrereleaseOrDraft(bool prerelease, bool draft)
    {
        var client = ClientReturning(ReleaseJson("v0.11.0", prerelease: prerelease, draft: draft));

        var update = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));

        Assert.Null(update);
    }

    [Fact]
    public async Task ReturnsNull_WhenTagIsNotParsable()
    {
        var client = ClientReturning(ReleaseJson("v0.11.0-beta"));

        var update = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));

        Assert.Null(update);
    }

    [Fact]
    public async Task ReturnsNull_WhenHtmlUrlIsMissingOrInvalid()
    {
        var client = ClientReturning(ReleaseJson("v0.11.0", htmlUrl: "not a url"));

        var update = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));

        Assert.Null(update);
    }

    [Fact]
    public async Task ReturnsNull_OnHttpFailure_AndDoesNotCacheIt()
    {
        var failure = ClientReturning("", HttpStatusCode.InternalServerError);

        Assert.Null(await UpdateChecker.GetLatestUpdateAsync(failure, Version.Parse("0.10.0.0")));

        // A failed fetch must not be cached: a subsequent successful check
        // still reaches the network and reports the update.
        var success = ClientReturning(ReleaseJson("v0.11.0"));
        Assert.NotNull(await UpdateChecker.GetLatestUpdateAsync(success, Version.Parse("0.10.0.0")));
    }

    [Fact]
    public async Task CachesTheResult_SoRepeatChecksSkipTheNetwork()
    {
        var calls = 0;
        var client = ClientReturning(ReleaseJson("v0.11.0"), onCall: _ => Interlocked.Increment(ref calls));

        var first = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));
        var second = await UpdateChecker.GetLatestUpdateAsync(client, Version.Parse("0.10.0.0"));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }

    /// <summary>Minimal HttpMessageHandler stub: returns a canned response.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
