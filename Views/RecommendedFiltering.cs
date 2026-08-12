using LlamaApp.HuggingFace;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure filtering rules for the Recommended Models list — kept separate
    /// from <see cref="MainWindow"/> so they stay unit-testable, mirroring
    /// <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class RecommendedFiltering
    {
        /// <summary>
        /// Only families the catalog marks <c>featured</c> are rendered;
        /// catalog order is preserved (LINQ's Where is stable). The filter
        /// lives here — not in the catalog fetch — because the same catalog
        /// also enriches the Available list, whose downloaded rows keep their
        /// metadata whether or not their family is featured.
        /// </summary>
        public static List<Repository> FilterForDisplay(IEnumerable<Repository> repos)
            => repos.Where(r => r.Featured).ToList();
    }
}
