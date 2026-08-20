using System.ComponentModel;

namespace LlamaApp.Views
{
    /// <summary>The kind of row a <see cref="ModelListItemViewModel"/> renders.</summary>
    public enum ModelListItemKind
    {
        /// <summary>A concrete installed model (run/delete actions).</summary>
        InstalledModel,

        /// <summary>The subtle "Browse more" separator between the two groups.</summary>
        BrowseSeparator,

        /// <summary>A catalog model family (opens the family details view).</summary>
        ModelFamily,
    }

    /// <summary>
    /// Base view-model for one row of the single models list. The list mixes
    /// installed model rows, the "Browse more" separator and catalog family
    /// rows; a <see cref="ModelListTemplateSelector"/> maps each concrete type
    /// to its XAML template.
    ///
    /// <para>Overriding <see cref="object.ToString"/> gives each row a
    /// sensible screen-reader name even where the visual content is a
    /// template (ListViewItems fall back to ToString for their automation
    /// name).</para>
    /// </summary>
    public abstract class ModelListItemViewModel : INotifyPropertyChanged
    {
        private bool _showDivider;

        /// <summary>The row kind, for the template selector and divider logic.</summary>
        public abstract ModelListItemKind Kind { get; }

        /// <summary>
        /// Whether the subtle 1px divider renders at the top of this row.
        /// Set by the shell when the list structure changes: dividers sit
        /// between content rows only — never adjacent to the separator.
        /// </summary>
        public bool ShowDivider
        {
            get => _showDivider;
            set
            {
                if (_showDivider == value) return;
                _showDivider = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDivider)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>An installed model row — wraps the shared <see cref="Views.ModelItem"/>.</summary>
    public sealed class InstalledModelListItemViewModel : ModelListItemViewModel
    {
        public InstalledModelListItemViewModel(ModelItem model)
        {
            Model = model;
        }

        public override ModelListItemKind Kind => ModelListItemKind.InstalledModel;

        /// <summary>The row's model — the same instance the rest of the app drives.</summary>
        public ModelItem Model { get; }

        public override string ToString() => Model.RowAccessibleName;
    }

    /// <summary>The lightweight "Browse more" separator between installed and catalog models.</summary>
    public sealed class BrowseSeparatorListItemViewModel : ModelListItemViewModel
    {
        public override ModelListItemKind Kind => ModelListItemKind.BrowseSeparator;

        public override string ToString() => "Browse more";
    }

    /// <summary>A catalog model family row — wraps a <see cref="ModelFamilyViewModel"/>.</summary>
    public sealed class ModelFamilyListItemViewModel : ModelListItemViewModel
    {
        public ModelFamilyListItemViewModel(ModelFamilyViewModel family)
        {
            Family = family;
        }

        public override ModelListItemKind Kind => ModelListItemKind.ModelFamily;

        /// <summary>The family shown by the row (also the details view's source).</summary>
        public ModelFamilyViewModel Family { get; }

        public override string ToString() => Family.AccessibleName;
    }
}
