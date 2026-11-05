namespace LlamaApp.Views;

/// <summary>
/// Non-visual core of dismissing the Hub search results. The results panel is
/// owned by <see cref="MainWindow"/>; this helper keeps the request-lifetime
/// rule independently testable without instantiating WinUI controls.
/// </summary>
internal static class HubSearchDismissal
{
    /// <summary>
    /// Cancels the active full-search/page request and returns the next search
    /// generation. Advancing the generation is required even when cancellation
    /// wins the race: a response that has already completed must fail the
    /// caller's generation check rather than mutating the dismissed results.
    /// </summary>
    internal static int Invalidate(int currentSearchId, CancellationTokenSource? inFlight)
    {
        inFlight?.Cancel();
        return unchecked(currentSearchId + 1);
    }
}
