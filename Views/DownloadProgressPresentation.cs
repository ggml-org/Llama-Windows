namespace LlamaApp.Views
{
    /// <summary>
    /// Pure formatting/speed helpers for the download-progress detail line —
    /// kept separate from <see cref="MainWindow"/> and <see cref="ModelItem"/>
    /// so the rules are unit-testable (no XAML objects involved), mirroring
    /// <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class DownloadProgressPresentation
    {
        /// <summary>
        /// Formats a byte count as a human-readable size, e.g. 2526080992 →
        /// "2.5 GB". Decimal units (1 GB = 1e9 B) with one fractional digit at
        /// GB/TB — matching the catalog's pre-formatted size strings, so the
        /// progress detail agrees with the size shown before the download.
        /// </summary>
        public static string FormatBytes(double bytes)
        {
            if (bytes >= 1_000_000_000_000) return $"{bytes / 1_000_000_000_000.0:0.#} TB";
            if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000.0:0.#} GB";
            if (bytes >= 1_000_000) return $"{bytes / 1_000_000.0:0} MB";
            if (bytes >= 1_000) return $"{bytes / 1_000.0:0} KB";
            return $"{(long)bytes} B";
        }

        /// <summary>
        /// Builds the download detail line shown in place of the row's
        /// "params · size" subtitle while a download runs:
        /// "3.2 GB of 12.1 GB", plus " · 45 MB/s" once a speed estimate
        /// exists, plus " · ~4 min left" once the ETA is meaningful. Segments
        /// appear only when their inputs are known — a stalled stream (speed
        /// 0) never shows a bogus "0 MB/s · ∞ left".
        /// </summary>
        public static string FormatDetail(long downloadedBytes, long totalBytes, double bytesPerSecond)
        {
            var text = $"{FormatBytes(downloadedBytes)} of {FormatBytes(totalBytes)}";
            if (bytesPerSecond <= 0) return text;

            text += $" · {FormatBytes(bytesPerSecond)}/s";

            var remainingBytes = totalBytes - downloadedBytes;
            if (remainingBytes > 0)
                text += $" · {FormatEta(remainingBytes / bytesPerSecond)}";
            return text;
        }

        /// <summary>
        /// Coarse ETA label: "~30 s left" under a minute, "~N min left" up to
        /// 90 minutes, "~N h left" beyond. Deliberately imprecise — a
        /// second-accurate countdown would lie about a fluctuating stream.
        /// </summary>
        public static string FormatEta(double seconds)
        {
            if (seconds < 60) return $"~{Math.Max(1, (int)Math.Round(seconds))} s left";
            var minutes = seconds / 60;
            if (minutes < 90) return $"~{Math.Max(1, (int)Math.Round(minutes))} min left";
            return $"~{(int)Math.Round(minutes / 60)} h left";
        }

        /// <summary>
        /// Exponentially-smoothed download speed (alpha 0.3): the SSE stream's
        /// per-chunk events are bursty, so the instantaneous rate between two
        /// throttled UI samples jitters wildly. The first sample seeds the
        /// average outright.
        /// </summary>
        public static double SmoothSpeed(double previousBytesPerSecond, double instantaneousBytesPerSecond)
            => previousBytesPerSecond <= 0
                ? instantaneousBytesPerSecond
                : previousBytesPerSecond * 0.7 + instantaneousBytesPerSecond * 0.3;
    }
}
