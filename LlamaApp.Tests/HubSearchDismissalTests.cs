using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Regression coverage for dismissing Hub search results while a full search
/// or page request is still in flight.
/// </summary>
public sealed class HubSearchDismissalTests
{
    [Fact]
    public void Dismiss_CancelsTheRequestAndInvalidatesItsGeneration()
    {
        using var request = new CancellationTokenSource();
        const int responseSearchId = 41;

        var currentSearchId = HubSearchDismissal.Invalidate(responseSearchId, request);

        Assert.True(request.IsCancellationRequested);
        Assert.Equal(42, currentSearchId);
        // RunHubSearchAsync and HubLoadMore_Click both use this generation
        // comparison before touching the results or their visibility.
        Assert.NotEqual(responseSearchId, currentSearchId);
    }

    [Fact]
    public void Dismiss_WithoutAnActiveRequestStillInvalidatesCompletedResponses()
    {
        const int alreadyCompletedSearchId = 7;

        var currentSearchId = HubSearchDismissal.Invalidate(
            alreadyCompletedSearchId, inFlight: null);

        Assert.Equal(8, currentSearchId);
        Assert.NotEqual(alreadyCompletedSearchId, currentSearchId);
    }
}
