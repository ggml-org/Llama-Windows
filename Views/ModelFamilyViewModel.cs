using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LlamaApp.HuggingFace;
using Microsoft.UI.Xaml.Media;

namespace LlamaApp.Views
{
    /// <summary>
    /// View-model for one catalog model family row ("Gemma 3") and the source
    /// of the family details view. Deliberately variant-independent at the
    /// row level: no quantization, file size, license or ranking — the row
    /// only says "this family exists, in these parameter sizes; open it to
    /// inspect and install". The sizes and their builds live on the wrapped
    /// <see cref="ModelFamily"/> for the details view.
    /// </summary>
    public sealed class ModelFamilyViewModel : INotifyPropertyChanged
    {
        /// <summary>How many parameter sizes fit on the row before truncating.</summary>
        private const int MaxDisplayedSizes = 5;

        private ImageSource? _logo;

        /// <summary>
        /// <paramref name="logoResolver"/> is injectable so tests can build a
        /// family view-model without touching the XAML logo pipeline
        /// (<see cref="ModelItem.ResolveLogo"/> rasterizes an SVG and needs
        /// the running app); the shell passes the real resolver.
        /// </summary>
        public ModelFamilyViewModel(ModelFamily family, Func<string?, ImageSource?>? logoResolver = null)
        {
            Family = family;
            _logo = logoResolver?.Invoke(family.Brand);
        }

        /// <summary>The wrapped catalog family (stable identity + sizes/builds).</summary>
        public ModelFamily Family { get; }

        /// <summary>Family display name, e.g. "Gemma 3".</summary>
        public string Name => Family.Name;

        /// <summary>One-line description, e.g. "Google's multimodal model family."</summary>
        public string Description => Family.Description;

        /// <summary>Brand label (drives the logo), e.g. "Google".</summary>
        public string Brand => Family.Brand;

        /// <summary>
        /// The resolved brand logo (theme-dependent — see
        /// <see cref="ModelItem.ResolveLogo"/>). Notifies so the shell can
        /// re-resolve logos when the OS theme flips.
        /// </summary>
        public ImageSource? Logo
        {
            get => _logo;
            set
            {
                if (ReferenceEquals(_logo, value)) return;
                _logo = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The inline size list for the row, e.g. "270M · 1B · 4B · 12B ·
        /// 27B" — capped so a long family trims gracefully instead of
        /// wrapping ("· …" marks the cut).
        /// </summary>
        public string DisplayParameterSizes => FormatParameterSizes(Family.Sizes);

        /// <summary>
        /// The row's accessible name, e.g. "Gemma 3. Available sizes: 270M,
        /// 1B, 4B, 12B, 27B. Open details." (ListViewItems fall back to the
        /// item's ToString, which the list-item wrapper overrides to this.)
        /// </summary>
        public string AccessibleName
        {
            get
            {
                var sizes = string.Join(", ", SizeLabels);
                return string.IsNullOrEmpty(sizes)
                    ? Name + ". Open details."
                    : Name + ". Available sizes: " + sizes + ". Open details.";
            }
        }

        /// <summary>
        /// The row tooltip: the family name plus what it is — and the
        /// <see cref="FitNote"/> on a last line when the fit evaluation
        /// flagged every build as too big for this machine. Notifies (via
        /// <see cref="FitsOnDevice"/>/<see cref="FitNote"/>) because the fit
        /// evaluation lands after the rows are rendered.
        /// </summary>
        public string RowToolTip
        {
            get
            {
                var head = string.IsNullOrWhiteSpace(Description)
                    ? Name
                    : Name + "\n" + Description;
                return string.IsNullOrWhiteSpace(FitNote)
                    ? head
                    : head + "\n" + FitNote;
            }
        }

        // ---- Device-fit state (dims family rows the machine can't run) ----
        // Mirrors ModelItem's fit state: the evaluation
        // (MainWindow.EvaluateFamilyFitsAsync) lands after the rows render.

        private bool _fitsOnDevice = true;
        private string? _fitNote;

        /// <summary>
        /// Whether this machine can run any build of the family — defaults to
        /// <c>true</c>: an unknown machine size must never dim a row (fail
        /// open, same as the download preflight). A <c>false</c> family
        /// renders dimmed (<see cref="RowOpacity"/>) but stays clickable —
        /// the preflight still has the final say on download.
        /// </summary>
        public bool FitsOnDevice
        {
            get => _fitsOnDevice;
            set
            {
                if (_fitsOnDevice == value) return;
                _fitsOnDevice = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RowOpacity));
                OnPropertyChanged(nameof(RowToolTip));
            }
        }

        /// <summary>
        /// Short reason the family doesn't fit, surfaced as the tooltip's
        /// last line; <c>null</c> while some build fits or the evaluation
        /// hasn't run.
        /// </summary>
        public string? FitNote
        {
            get => _fitNote;
            set
            {
                if (_fitNote == value) return;
                _fitNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RowToolTip));
            }
        }

        /// <summary>Row opacity: a family the machine can't run renders dimmed.</summary>
        public double RowOpacity => FitsOnDevice ? 1.0 : 0.4;

        private IEnumerable<string> SizeLabels => Family.Sizes
            .Select(s => s.Params)
            .Where(p => !string.IsNullOrWhiteSpace(p));

        /// <summary>
        /// Joins a family's size labels with " · ", keeping at most
        /// <see cref="MaxDisplayedSizes"/> and appending " · …" when more
        /// exist. Pure — kept public static so it stays unit-testable.
        /// </summary>
        public static string FormatParameterSizes(IReadOnlyList<ModelFamilySize> sizes)
        {
            var labels = sizes.Select(s => s.Params)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var text = string.Join(" · ", labels.Take(MaxDisplayedSizes));
            if (labels.Count > MaxDisplayedSizes)
                text += " · …";
            return text;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
