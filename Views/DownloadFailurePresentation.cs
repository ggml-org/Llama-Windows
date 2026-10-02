using LlamaApp.Common;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure presentation helpers for download failures — the classification
    /// boundary and the toast body. Kept separate from <see cref="MainWindow"/>
    /// and <see cref="ModelItem"/> so the rules are unit-testable (no XAML
    /// objects involved), mirroring
    /// <see cref="DownloadProgressPresentation"/>.
    ///
    /// <para>This is the boundary that decides what is a failure at all: a
    /// cancellation report (<c>Failed:false</c>) or a completion never yields a
    /// <see cref="DownloadFailure"/>, so cancellation can never present as a
    /// failure.</para>
    /// </summary>
    public static class DownloadFailurePresentation
    {
        /// <summary>Preserves the existing toast-detail truncation rule.</summary>
        private const int RawDetailLimit = 140;

        /// <summary>
        /// Classifies the failure described by <paramref name="report"/>, or
        /// returns <c>null</c> when the report isn't a failure (a completion or
        /// a cancellation — <c>Failed:false</c>). Fail-soft: a classification
        /// problem yields an Unknown fallback that still carries the raw detail
        /// rather than throwing into the toast/row path.
        /// </summary>
        public static DownloadFailure? ClassifyReport(ModelDownloadProgress report)
            => report.Failed
                ? Classify(report.HttpStatus, report.Message, report.ExceptionType)
                : null;

        /// <summary>
        /// Classifies a download failure from its raw inputs (HTTP status of a
        /// rejected POST, raw error text, exception type). Fail-soft — never
        /// throws; the raw detail is always preserved on the result.
        /// </summary>
        public static DownloadFailure Classify(int? httpStatus, string? detail, string? exceptionType)
        {
            try
            {
                return DownloadFailureClassifier.Classify(httpStatus, detail, exceptionType);
            }
            catch
            {
                return new DownloadFailure(
                    DownloadFailureKind.Unknown,
                    "The download failed for an unknown reason.",
                    "Check the app log for details.",
                    detail);
            }
        }

        /// <summary>
        /// Builds the download-failure toast body: the headline + actionable
        /// guidance from the classified failure. The raw technical detail is
        /// appended only for the Unknown fallback (whose guidance is generic) —
        /// for known categories the detail stays in the row tooltip + app log.
        /// The raw detail append keeps the existing 140-char truncation.
        /// </summary>
        public static string ToastBody(string displayName, DownloadFailure? failure)
        {
            if (failure is null)
                return $"{displayName} couldn't be downloaded.";

            var body = $"{displayName} couldn't be downloaded. {failure.Headline} {failure.Guidance}";
            if (failure.Kind == DownloadFailureKind.Unknown && !string.IsNullOrWhiteSpace(failure.RawDetail))
                body += $" Server said: {Truncate(failure.RawDetail!)}.";
            return body;
        }

        private static string Truncate(string value)
            => value.Length > RawDetailLimit ? value[..RawDetailLimit] + "…" : value;
    }
}
