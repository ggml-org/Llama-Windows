namespace LlamaApp.Views;

/// <summary>
/// Pure pagination arithmetic for the Hub-search results list — kept free of
/// any UI types so the "is there possibly a next page" / "what is the next
/// skip offset" decisions are unit-testable. MainWindow is the only caller;
/// the shown-result count it passes excludes the load-more sentinel row.
/// </summary>
public static class HubSearchPagination
{
    /// <summary>
    /// True when the just-fetched page might have more results after it: the
    /// Hub returns a full page while more exist and a short (or empty) page at
    /// the end. A non-positive page size can never have a next page.
    /// </summary>
    public static bool PossiblyHasNextPage(int returnedCount, int pageSize)
        => pageSize > 0 && returnedCount >= pageSize;

    /// <summary>
    /// The 0-based offset for the next page: the number of real result rows
    /// shown so far (the load-more sentinel is excluded by the caller, so this
    /// is never the collection size). Negative input is clamped to 0.
    /// </summary>
    public static int NextSkip(int shownResultCount)
        => shownResultCount < 0 ? 0 : shownResultCount;
}
