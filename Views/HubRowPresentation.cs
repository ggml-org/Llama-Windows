namespace LlamaApp.Views;

/// <summary>
/// Pure presentation helpers for Hub search-result rows: compact count
/// formatting and relative repo age. Kept free of UI types so both are
/// unit-testable, mirroring <see cref="DownloadProgressPresentation"/>.
/// </summary>
public static class HubRowPresentation
{
    /// <summary>
    /// Compact counter, e.g. 999 → "999", 1234 → "1.2K", 12345 → "12K",
    /// 1234567 → "1.2M", 12345678 → "12.3M" — the usual Hub-counter style:
    /// decimal units (K/M/B), one fractional digit below 10, whole numbers at
    /// and above. Negative input renders as the raw number (the Hub doesn't
    /// send negatives; no reason to invent a format for them).
    /// </summary>
    public static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000.0:0}K",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString(),
    };

    /// <summary>
    /// The relative age of a repo's last update, for the row's metadata line —
    /// "just now", "42 minutes ago", "3 hours ago", "2 days ago",
    /// "5 weeks ago", "3 months ago", "2 years ago". Coarse buckets on
    /// purpose: the Hub timestamps are day-granular in practice, and the goal
    /// is recognizing a stale mirror at a glance, not precision. A future
    /// timestamp (clock skew) clamps to "just now"; a missing/unparseable one
    /// yields null so the metadata part drops out entirely.
    /// </summary>
    public static string? FormatAge(DateTimeOffset? lastModified, DateTimeOffset now)
    {
        if (lastModified is not { } then) return null;

        var delta = now - then;
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1))
            return Age(Math.Max(1, (long)delta.TotalMinutes), "minute");
        if (delta < TimeSpan.FromDays(1))
            return Age(Math.Max(1, (long)delta.TotalHours), "hour");
        if (delta < TimeSpan.FromDays(7))
            return Age(Math.Max(1, (long)delta.TotalDays), "day");
        if (delta < TimeSpan.FromDays(30))
            return Age(Math.Max(1, (long)(delta.TotalDays / 7)), "week");
        if (delta < TimeSpan.FromDays(365))
            return Age(Math.Max(1, (long)(delta.TotalDays / 30)), "month");
        return Age(Math.Max(1, (long)(delta.TotalDays / 365)), "year");
    }

    private static string Age(long count, string unit)
        => count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
