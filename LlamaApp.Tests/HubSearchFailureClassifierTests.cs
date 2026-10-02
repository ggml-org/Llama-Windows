using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="HubSearchFailureClassifier"/>: the pure, total
/// mapping from a Hub search failure's raw inputs (HTTP status, exception type
/// name, token-configured flag) to an actionable
/// <see cref="HubSearchFailure"/>.
/// </summary>
public class HubSearchFailureClassifierTests
{
    private static string Text(HubSearchFailure f) => $"{f.Headline} {f.Guidance}";

    // ----- Rate limited (429) ----------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rate_limited_status_classifies_as_rate_limited(bool tokenConfigured)
    {
        var failure = HubSearchFailureClassifier.Classify(429, null, tokenConfigured);

        Assert.Equal(HubSearchFailureKind.RateLimited, failure.Kind);
        Assert.Contains("rate-limited", failure.Headline, StringComparison.OrdinalIgnoreCase);
        // Guidance points at the Settings token as the fix.
        Assert.Contains("token", failure.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", failure.Guidance);
    }

    // ----- Invalid token (401/403) -----------------------------------------

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Auth_status_with_token_configured_classifies_as_invalid_token(int status)
    {
        var failure = HubSearchFailureClassifier.Classify(status, null, tokenConfigured: true);

        Assert.Equal(HubSearchFailureKind.InvalidToken, failure.Kind);
        Assert.Contains("token", failure.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", failure.Guidance);
        Assert.Contains("invalid", failure.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Auth_status_without_token_does_not_show_token_guidance(int status)
    {
        var failure = HubSearchFailureClassifier.Classify(status, null, tokenConfigured: false);

        // No token to blame — fall back to the generic bucket, never token advice.
        Assert.Equal(HubSearchFailureKind.Unknown, failure.Kind);
        Assert.Equal("Search failed. Try again.", Text(failure));
        Assert.DoesNotContain("token", Text(failure), StringComparison.OrdinalIgnoreCase);
    }

    // ----- Other parseable statuses ----------------------------------------

    [Theory]
    [InlineData(500, true)]
    [InlineData(500, false)]
    [InlineData(404, true)]
    [InlineData(503, false)]
    public void Other_status_classifies_as_unknown(int status, bool tokenConfigured)
    {
        var failure = HubSearchFailureClassifier.Classify(status, null, tokenConfigured);

        Assert.Equal(HubSearchFailureKind.Unknown, failure.Kind);
        Assert.Equal("Search failed. Try again.", Text(failure));
    }

    // ----- Network transport failures (frozen wording) ---------------------

    [Theory]
    [InlineData("HttpRequestException")]
    [InlineData("TaskCanceledException")]
    [InlineData("TimeoutException")]
    [InlineData("HTTPREQUESTEXCEPTION")] // case-insensitive match
    public void Transport_exception_types_keep_the_frozen_network_wording(string exceptionType)
    {
        var failure = HubSearchFailureClassifier.Classify(null, exceptionType, tokenConfigured: false);

        Assert.Equal(HubSearchFailureKind.Network, failure.Kind);
        Assert.Equal(
            "Couldn't reach Hugging Face. Check your connection and try again.",
            Text(failure));
    }

    // ----- Unknown fallback (frozen wording) -------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "SocketException")]
    [InlineData(null, "SomeWeirdException")]
    public void Unrecognized_failures_keep_the_frozen_unknown_wording(int? status, string? exceptionType)
    {
        var failure = HubSearchFailureClassifier.Classify(status, exceptionType, tokenConfigured: true);

        Assert.Equal(HubSearchFailureKind.Unknown, failure.Kind);
        Assert.Equal("Search failed. Try again.", Text(failure));
    }

    // ----- Totality: never throws, always non-empty -------------------------

    [Fact]
    public void Classifier_is_total_over_a_matrix_of_garbage_inputs()
    {
        int?[] statuses = [null, -1, 0, 200, 429, 401, 403, 404, 500, 599, int.MaxValue];
        string?[] exceptionTypes =
            [null, "", "HttpRequestException", "TaskCanceledException", "TimeoutException",
             "SocketException", "  ", "\u0000", "nonsense"];

        foreach (var status in statuses)
        {
            foreach (var exceptionType in exceptionTypes)
            {
                foreach (var tokenConfigured in new[] { true, false })
                {
                    var failure = HubSearchFailureClassifier.Classify(status, exceptionType, tokenConfigured);

                    Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
                    Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
                    // A status always wins over the exception name.
                    if (status is 429)
                        Assert.Equal(HubSearchFailureKind.RateLimited, failure.Kind);
                }
            }
        }
    }
}
