using LlamaApp.Common;
using LlamaApp.HuggingFace;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="HubSearchFailurePresentation"/> — the fail-soft
/// classification boundary from a caught exception and the status-line/no-results
/// text, including the frozen network/unknown wording.
/// </summary>
public class HubSearchFailurePresentationTests
{
    // ----- Classification from a caught exception ---------------------------

    [Fact]
    public void Typed_status_429_classifies_as_rate_limited()
    {
        var failure = HubSearchFailurePresentation.Classify(new HubSearchException(429), tokenConfigured: false);

        Assert.Equal(HubSearchFailureKind.RateLimited, failure.Kind);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Typed_auth_status_depends_on_token_configured(int status)
    {
        Assert.Equal(
            HubSearchFailureKind.InvalidToken,
            HubSearchFailurePresentation.Classify(new HubSearchException(status), tokenConfigured: true).Kind);
        Assert.Equal(
            HubSearchFailureKind.Unknown,
            HubSearchFailurePresentation.Classify(new HubSearchException(status), tokenConfigured: false).Kind);
    }

    [Fact]
    public void Typed_server_status_classifies_as_unknown()
    {
        var failure = HubSearchFailurePresentation.Classify(new HubSearchException(500), tokenConfigured: true);

        Assert.Equal(HubSearchFailureKind.Unknown, failure.Kind);
    }

    [Fact]
    public void Plain_transport_exceptions_classify_as_network_with_frozen_text()
    {
        Assert.Equal(
            "Couldn't reach Hugging Face. Check your connection and try again.",
            HubSearchFailurePresentation.StatusText(
                HubSearchFailurePresentation.Classify(new HttpRequestException("boom"), tokenConfigured: false)));

        Assert.Equal(HubSearchFailureKind.Network,
            HubSearchFailurePresentation.Classify(new TaskCanceledException(), true).Kind);
        Assert.Equal(HubSearchFailureKind.Network,
            HubSearchFailurePresentation.Classify(new TimeoutException(), true).Kind);
    }

    [Fact]
    public void Unrelated_exception_classifies_as_unknown_with_frozen_text()
    {
        var failure = HubSearchFailurePresentation.Classify(new InvalidOperationException(), tokenConfigured: true);

        Assert.Equal(HubSearchFailureKind.Unknown, failure.Kind);
        Assert.Equal("Search failed. Try again.", HubSearchFailurePresentation.StatusText(failure));
    }

    [Fact]
    public void Classify_is_fail_soft_on_malformed_input()
    {
        // A null exception must not throw out of the presentation boundary.
        var failure = HubSearchFailurePresentation.Classify(null!, tokenConfigured: true);

        Assert.Equal(HubSearchFailureKind.Unknown, failure.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
        Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
    }

    // ----- Status text composition ------------------------------------------

    [Fact]
    public void Status_text_is_headline_space_guidance()
    {
        var failure = new HubSearchFailure(HubSearchFailureKind.RateLimited, "Head.", "Guide.");

        Assert.Equal("Head. Guide.", HubSearchFailurePresentation.StatusText(failure));
    }

    // ----- No-results caption -----------------------------------------------

    [Fact]
    public void No_results_caption_keeps_prefix_and_appends_one_nudge()
    {
        var caption = HubSearchFailurePresentation.NoResultsCaption("gemma");

        Assert.Equal(
            "No GGUF models found for \u201Cgemma\u201D. Try a shorter or more general search.",
            caption);
    }
}
