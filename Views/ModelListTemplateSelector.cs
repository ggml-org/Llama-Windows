using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LlamaApp.Views
{
    /// <summary>
    /// Maps each <see cref="ModelListItemViewModel"/> to its row template —
    /// the single <see cref="ListView"/> renders installed model rows, the
    /// "Browse more" separator and catalog family rows from one collection
    /// (one scroll surface, one virtualization panel).
    /// </summary>
    public sealed class ModelListTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? InstalledModelTemplate { get; set; }
        public DataTemplate? BrowseSeparatorTemplate { get; set; }
        public DataTemplate? ModelFamilyTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            switch (item)
            {
                case InstalledModelListItemViewModel when InstalledModelTemplate != null:
                    return InstalledModelTemplate;
                case BrowseSeparatorListItemViewModel when BrowseSeparatorTemplate != null:
                    return BrowseSeparatorTemplate;
                case ModelFamilyListItemViewModel when ModelFamilyTemplate != null:
                    return ModelFamilyTemplate;
                default:
                    return base.SelectTemplateCore(item);
            }
        }
    }
}
