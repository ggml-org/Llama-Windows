namespace LlamaApp.Views
{
    /// <summary>
    /// Pure rebuild policy for the models list's browse tail (the "Browse
    /// more" separator + catalog family rows) — kept separate from
    /// <see cref="MainWindow"/> so it stays unit-testable, mirroring
    /// <see cref="RecommendedFiltering"/>. The tail's desired content depends
    /// only on the family rows and on whether an installed section exists to
    /// separate from them; every other list change (a row added or removed
    /// anywhere) must leave the tail's realized containers alone instead of
    /// tearing the rows down and re-adding them.
    /// </summary>
    public static class BrowseTailRefresh
    {
        /// <summary>
        /// Whether the tail must be rebuilt, given what it was last built from
        /// (<paramref name="builtFrom"/>/<paramref name="builtWithSeparator"/>;
        /// <c>null</c> means never built) and what it should contain now
        /// (<paramref name="families"/>/<paramref name="withSeparator"/>).
        /// Family rows are compared by reference and order — a replacement or
        /// a reorder is a rebuild, an unrelated list mutation is not.
        /// </summary>
        public static bool NeedsRebuild<T>(
            IReadOnlyList<T>? builtFrom,
            IReadOnlyList<T> families,
            bool builtWithSeparator,
            bool withSeparator)
        {
            if (builtFrom is null ||
                builtWithSeparator != withSeparator ||
                builtFrom.Count != families.Count)
                return true;

            for (var i = 0; i < families.Count; i++)
                if (!ReferenceEquals(builtFrom[i], families[i]))
                    return true;

            return false;
        }
    }
}
