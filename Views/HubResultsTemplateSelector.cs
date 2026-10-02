using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LlamaApp.Views
{
    /// <summary>
    /// Maps each Hub-search results row to its template: a model row or the
    /// synthetic "Show more" / loading / retry row appended after a full page.
    /// Keeping the sentinel on its own template means the model row's live
    /// download button never renders for it.
    /// </summary>
    public sealed class HubResultsTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? ModelTemplate { get; set; }
        public DataTemplate? LoadMoreTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
            => item is HubModelItemViewModel { IsLoadMoreRow: true } && LoadMoreTemplate != null
                ? LoadMoreTemplate
                : ModelTemplate;
    }
}
