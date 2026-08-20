using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LlamaApp.HuggingFace;
using Microsoft.UI.Xaml.Media;

namespace LlamaApp.Views
{
    /// <summary>
    /// The shell operations the family details ViewModel needs (implemented
    /// by <c>MainWindow</c>): an installed check so a variant that's already
    /// downloaded reads as installed instead of offering a duplicate
    /// download, the existing download workflow (single implementation), and
    /// closing the details view. Faked in unit tests.
    /// </summary>
    public interface IModelFamilyDetailsHost
    {
        /// <summary>True when this exact build (repo + quant) is already installed.</summary>
        bool IsVariantInstalled(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build);

        /// <summary>Starts the shell's normal download workflow for the chosen variant.</summary>
        void DownloadVariant(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build);

        /// <summary>Closes the details view, returning to the model list.</summary>
        void CloseDetails();
    }

    /// <summary>A selectable parameter size of the family (the "context ladder" cells).</summary>
    public sealed class ModelFamilySizeOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ModelFamilySizeOption(ModelFamilySize size)
        {
            Size = size;
        }

        /// <summary>The wrapped catalog size (builds live here for the variant list).</summary>
        public ModelFamilySize Size { get; }

        /// <summary>The cell label: the parameter count, e.g. "4B".</summary>
        public string Label => Size.Params;

        /// <summary>The full size name, e.g. "Gemma 3 4B" (cell tooltip).</summary>
        public string FullName => Size.Name;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// A downloadable variant (one quant) of the selected size. The row leads
    /// with the friendly quality name; the raw quant + download size ride
    /// along as secondary text so the technical identifier stays visible.
    /// The whole row is the download button.
    /// </summary>
    public sealed class ModelFamilyVariantOption
    {
        public ModelFamilyVariantOption(
            ModelFamilySize size, ModelFamilyBuild build, bool isDefault, bool isInstalled)
        {
            Size = size;
            Build = build;
            IsDefault = isDefault;
            IsInstalled = isInstalled;
        }

        public ModelFamilySize Size { get; }
        public ModelFamilyBuild Build { get; }

        /// <summary>Friendly quality name ("Balanced", "Higher quality", …).</summary>
        public string FriendlyName => QuantizationPresentation.FriendlyName(Build.Quant);

        /// <summary>Secondary detail: the raw quant + download size, e.g. "Q4_K_M · 12.1 GB".</summary>
        public string DetailText => string.Join(" · ",
            new[] { Build.Quant, Build.Size }.Where(s => !string.IsNullOrWhiteSpace(s)));

        /// <summary>
        /// The catalog's first build of the size — marked with a subtle
        /// "Default" caption so there's an obvious sane pick (no provider
        /// ranking beyond catalog order).
        /// </summary>
        public bool IsDefault { get; }

        /// <summary>Installed variants read as such and can't be re-downloaded.</summary>
        public bool IsInstalled { get; }

        /// <summary>Whether the row can be invoked (installed variants can't).</summary>
        public bool IsSelectable => !IsInstalled;

        /// <summary>The caption replacing "Default" once the variant is on disk.</summary>
        public string StateCaption => IsInstalled ? "Installed" : IsDefault ? "Default" : "";

        /// <summary>The row's accessible name, e.g. "Balanced, Q4_K_M · 2.5 GB, default. Download."</summary>
        public string AccessibleName
        {
            get
            {
                var parts = new List<string> { FriendlyName };
                if (!string.IsNullOrEmpty(DetailText)) parts.Add(DetailText);
                if (StateCaption.Length > 0) parts.Add(StateCaption.ToLowerInvariant());
                parts.Add(IsInstalled ? "Already installed." : "Download.");
                return string.Join(", ", parts);
            }
        }
    }

    /// <summary>
    /// ViewModel for the family details view — the size → variant → download
    /// hierarchy behind a catalog family row. Cheap to construct (no I/O):
    /// everything comes from the already-fetched <see cref="ModelFamily"/>.
    /// All members are touched on the UI thread, mirroring
    /// <see cref="ModelItemDetailsViewModel"/>.
    /// </summary>
    public sealed class ModelFamilyDetailsViewModel : INotifyPropertyChanged
    {
        private readonly IModelFamilyDetailsHost _host;
        private ModelFamilySizeOption? _selectedSize;
        private bool _isBusy;

        public ModelFamilyDetailsViewModel(ModelFamily family, IModelFamilyDetailsHost host)
        {
            Family = family;
            _host = host;

            foreach (var size in family.Sizes)
                Sizes.Add(new ModelFamilySizeOption(size));

            // Neutral preselection: the catalog's first size (catalog order is
            // the only ordering policy — no quality ranking at this level).
            SelectedSize = Sizes.FirstOrDefault();
        }

        /// <summary>The family whose details are shown.</summary>
        public ModelFamily Family { get; }

        public string Name => Family.Name;
        public string Description => Family.Description;
        public bool HasDescription => !string.IsNullOrWhiteSpace(Family.Description);

        /// <summary>The resolved brand logo for the header (theme-dependent).</summary>
        public ImageSource? Logo => ModelItem.ResolveLogo(Family.Brand);

        // ---- Technical details (secondary — below the actions) ----

        public string Provider => string.IsNullOrWhiteSpace(Family.Brand) ? "Unknown" : Family.Brand;
        public string License => string.IsNullOrWhiteSpace(Family.License) ? "Unknown" : Family.License;
        public string Format => "GGUF";

        // ---- Size selection ----

        /// <summary>The family's sizes as selectable cells (catalog order).</summary>
        public ObservableCollection<ModelFamilySizeOption> Sizes { get; } =
            new ObservableCollection<ModelFamilySizeOption>();

        /// <summary>The selected size; selecting one rebuilds the variant list.</summary>
        public ModelFamilySizeOption? SelectedSize
        {
            get => _selectedSize;
            private set
            {
                if (ReferenceEquals(_selectedSize, value)) return;
                if (_selectedSize != null) _selectedSize.IsSelected = false;
                _selectedSize = value;
                if (value != null) value.IsSelected = true;
                OnPropertyChanged();
                RebuildVariants();
            }
        }

        /// <summary>Selects a size cell (no-op for foreign options).</summary>
        public void SelectSize(ModelFamilySizeOption option)
        {
            if (!Sizes.Contains(option)) return;
            SelectedSize = option;
        }

        // ---- Variants of the selected size ----

        /// <summary>The downloadable builds of the selected size (catalog order).</summary>
        public ObservableCollection<ModelFamilyVariantOption> Variants { get; } =
            new ObservableCollection<ModelFamilyVariantOption>();

        public bool HasVariants => Variants.Count > 0;

        private void RebuildVariants()
        {
            Variants.Clear();
            var size = _selectedSize?.Size;
            if (size == null)
            {
                OnPropertyChanged(nameof(HasVariants));
                return;
            }

            var first = true;
            foreach (var build in size.Builds)
            {
                Variants.Add(new ModelFamilyVariantOption(
                    size,
                    build,
                    isDefault: first,
                    isInstalled: _host.IsVariantInstalled(Family, size, build)));
                first = false;
            }
            OnPropertyChanged(nameof(HasVariants));
        }

        // ---- Download ----

        /// <summary>True while a download request is in flight.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Starts the shell's download workflow for a variant, then closes
        /// the details view when the download actually started (the row's
        /// progress ring in the list takes over the story). Installed
        /// variants are a no-op.
        /// </summary>
        public void DownloadVariant(ModelFamilyVariantOption variant)
        {
            if (IsBusy || variant.IsInstalled || !Variants.Contains(variant)) return;
            IsBusy = true;
            try
            {
                _host.DownloadVariant(Family, variant.Size, variant.Build);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
