using LlamaApp.Common;
using LlamaApp.HuggingFace;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure presentation helpers for Hub search failures — the classification
    /// boundary and the status-line text. Kept separate from
    /// <see cref="MainWindow"/> so the rules are unit-testable (no XAML objects
    /// involved), mirroring <see cref="DownloadFailurePresentation"/>.
    /// </summary>
    public static class HubSearchFailurePresentation
    {
        /// <summary>
        /// Classifies a caught search exception into an actionable
        /// <see cref="HubSearchFailure"/>. Fail-soft — never throws; a
        /// classification problem yields the Unknown fallback. The typed
        /// <see cref="HubSearchException"/> carries the HTTP status; other
        /// exception types are classified by name (a plain
        /// <see cref="HttpRequestException"/> → Network).
        /// </summary>
        public static HubSearchFailure Classify(Exception ex, bool tokenConfigured)
        {
            try
            {
                var status = (ex as HubSearchException)?.Status;
                return HubSearchFailureClassifier.Classify(status, ex.GetType().Name, tokenConfigured);
            }
            catch
            {
                return new HubSearchFailure(
                    HubSearchFailureKind.Unknown, "Search failed.", "Try again.");
            }
        }

        /// <summary>
        /// The status-line text for a classified failure: headline + guidance.
        /// Yields the exact frozen sentences for Network and Unknown.
        /// </summary>
        public static string StatusText(HubSearchFailure failure)
            => $"{failure.Headline} {failure.Guidance}";

        /// <summary>
        /// The zero-result status caption: the frozen
        /// 'No GGUF models found for "&lt;query&gt;".' prefix plus one helpful
        /// nudge sentence so a no-results search isn't a dead end.
        /// </summary>
        public static string NoResultsCaption(string query)
            => $"No GGUF models found for \u201C{query}\u201D. Try a shorter or more general search.";
    }
}
