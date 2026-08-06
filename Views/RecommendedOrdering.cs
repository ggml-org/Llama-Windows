using LlamaApp.HuggingFace;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure ordering rules for the Recommended Models list — kept separate
    /// from <see cref="MainWindow"/> so they stay unit-testable, mirroring
    /// <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class RecommendedOrdering
    {
        /// <summary>
        /// Featured families first, catalog order preserved within each group
        /// (LINQ's OrderByDescending is stable). The catalog parses a
        /// <c>featured</c> flag per family that previously drove nothing.
        /// </summary>
        public static List<Repository> OrderForDisplay(IEnumerable<Repository> repos)
            => repos.OrderByDescending(r => r.Featured).ToList();
    }
}
