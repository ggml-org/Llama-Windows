using System.Collections.ObjectModel;
using LlamaApp.HuggingFace;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure filtering rules for the catalog browse list — kept separate
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

        /// <summary>
        /// The family-level counterpart of <see cref="FilterForDisplay"/>:
        /// only catalog families marked <c>featured</c> get a browse row,
        /// catalog order preserved. Catalog order is the display policy
        /// within a fit group — no quality ranking or provider endorsement.
        /// </summary>
        public static List<ModelFamily> FilterFamiliesForDisplay(IEnumerable<ModelFamily> families)
            => families.Where(f => f.Featured).ToList();

        /// <summary>
        /// Stable partition: items the machine can run first, the rest below
        /// — original order preserved within each group, so the catalog's
        /// curation keeps its say inside the fitting half. The verdict comes
        /// from the fit evaluation (<c>MainWindow.EvaluateFamilyFitsAsync</c>),
        /// which lands after the rows are rendered — hence the ordering is
        /// applied after the fact, not at populate time.
        /// </summary>
        public static List<T> PartitionFitFirst<T>(IEnumerable<T> items, Func<T, bool> fits)
        {
            var list = items.ToList();
            var result = list.Where(fits).ToList();
            result.AddRange(list.Where(item => !fits(item)));
            return result;
        }

        /// <summary>
        /// Reorders <paramref name="collection"/> to match
        /// <paramref name="order"/> (same elements, new sequence) using
        /// <see cref="ObservableCollection{T}.Move"/> rather than a
        /// clear-and-refill: Move translates to item-level change
        /// notifications, so the list re-flows the rows without tearing them
        /// down — no flash, and any row state (dimming, rings) survives the
        /// shuffle. No-op when the order already matches.
        /// </summary>
        public static void ApplyOrder<T>(ObservableCollection<T> collection, IReadOnlyList<T> order)
            where T : class
        {
            if (collection.Count != order.Count)
                throw new ArgumentException("order must contain exactly the collection's elements.");

            for (var target = 0; target < order.Count; target++)
            {
                var current = collection.IndexOf(order[target]);
                if (current < 0)
                    throw new ArgumentException("order must contain exactly the collection's elements.");
                // Items already pinned at positions < target are untouched:
                // Move only shifts the [target, current) range.
                if (current != target)
                    collection.Move(current, target);
            }
        }
    }
}
