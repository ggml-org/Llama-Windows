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

    /// <summary>
    /// The fit verdict for one variant (size + build): whether the build is
    /// estimated to fit this machine, and the user-facing reason when it
    /// doesn't (same wording the browse-list dimming and the download
    /// preflight flyout use — one story everywhere).
    /// </summary>
    public sealed record VariantFitVerdict(bool Fits, string? Note);

    /// <summary>A selectable parameter size of the family (the "context ladder" cells).</summary>
    public sealed class ModelFamilySizeOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _fitsOnDevice = true;
        private string? _fitNote;

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
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        /// <summary>
        /// False when NONE of the size's builds is estimated to fit this
        /// machine (the family-details counterpart of the browse-list family
        /// dimming — a size stays lit when any build fits, since the variant
        /// rows below carry the per-build verdicts). Default-true until the
        /// fit probe says otherwise; unknown never dims (fail open).
        /// </summary>
        public bool FitsOnDevice
        {
            get => _fitsOnDevice;
            set
            {
                if (_fitsOnDevice == value) return;
                _fitsOnDevice = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CellOpacity));
            }
        }

        /// <summary>Why the size doesn't fit (null when it does) — quotes the
        /// least-demanding build that still didn't fit.</summary>
        public string? FitNote
        {
            get => _fitNote;
            set
            {
                if (_fitNote == value) return;
                _fitNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CellTooltip));
            }
        }

        /// <summary>Cell tooltip: the size name, or the fit note when the
        /// size's builds fit nowhere.</summary>
        public string CellTooltip => string.IsNullOrWhiteSpace(FitNote) ? FullName : FitNote!;

        /// <summary>Cell opacity: dimmed when none of the builds fit.</summary>
        public double CellOpacity => FitsOnDevice ? 1.0 : 0.4;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// A downloadable variant (one quant) of the selected size. The row leads
    /// with the friendly quality name; the raw quant + download size ride
    /// along as secondary text so the technical identifier stays visible.
    /// The whole row is the download button.
    /// </summary>
    public sealed class ModelFamilyVariantOption : INotifyPropertyChanged
    {
        private bool _fitsOnDevice = true;
        private string? _fitNote;

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

        /// <summary>
        /// False when this build is estimated not to fit this machine — the
        /// row dims (but stays tappable: the download preflight then shows
        /// the same numbers in its flyout, matching the browse-list rows).
        /// Default-true until the fit probe says otherwise; a failed or
        /// missing probe never dims (fail open).
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
                OnPropertyChanged(nameof(AccessibleName));
            }
        }

        /// <summary>Why the variant doesn't fit (null when it does) — row tooltip.</summary>
        public string? FitNote
        {
            get => _fitNote;
            set
            {
                if (_fitNote == value) return;
                _fitNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RowTooltip));
            }
        }

        /// <summary>Row opacity: dimmed when the build fits nowhere (installed
        /// rows stay lit — what's on disk is on disk).</summary>
        public double RowOpacity => IsInstalled || FitsOnDevice ? 1.0 : 0.4;

        /// <summary>Row tooltip: the fit note, or nothing when the build fits.</summary>
        public string? RowTooltip => FitsOnDevice ? null : FitNote;

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
                if (!IsInstalled && !FitsOnDevice) parts.Add("May not fit on this machine.");
                parts.Add(IsInstalled ? "Already installed." : "Download.");
                return string.Join(", ", parts);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// ViewModel for the family details view — the size → variant → download
    /// hierarchy behind a catalog family row. Cheap to construct (no I/O):
    /// everything comes from the already-fetched <see cref="ModelFamily"/>.
    /// All members are touched on the UI thread, mirroring
    /// <see cref="ModelItemDetailsViewModel"/>.
    /// </summary>
    public sealed class ModelFamilyDetailsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IModelFamilyDetailsHost _host;
        private readonly Func<ModelFamilySize, ModelFamilyBuild, CancellationToken, Task<VariantFitVerdict>>? _variantFitProbe;
        private readonly Dictionary<ModelFamilyBuild, VariantFitVerdict> _fitVerdicts = new();
        private CancellationTokenSource? _fitCts;
        private ModelFamilySizeOption? _selectedSize;
        private bool _isBusy;

        public ModelFamilyDetailsViewModel(
            ModelFamily family, IModelFamilyDetailsHost host,
            Func<ModelFamilySize, ModelFamilyBuild, CancellationToken, Task<VariantFitVerdict>>? variantFitProbe = null)
        {
            Family = family;
            _host = host;
            _variantFitProbe = variantFitProbe;

            foreach (var size in family.Sizes)
                Sizes.Add(new ModelFamilySizeOption(size));

            // Neutral preselection: the catalog's first size (catalog order is
            // the only ordering policy — no quality ranking at this level).
            SelectedSize = Sizes.FirstOrDefault();

            // Fit verdicts land after the rows render (the probe awaits the
            // cached device list) — rows start lit and dim in place.
            if (variantFitProbe is not null)
            {
                _fitCts = new CancellationTokenSource();
                FitEvaluation = EvaluateAllFitsAsync(_fitCts.Token);
            }
        }

        /// <summary>
        /// The background fit evaluation (completed when no probe was
        /// injected) — the shell fire-and-forgets it; unit tests await it.
        /// Never faults: cancellation and probe failures are absorbed.
        /// </summary>
        internal Task FitEvaluation { get; } = Task.CompletedTask;

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
                var option = new ModelFamilyVariantOption(
                    size,
                    build,
                    isDefault: first,
                    isInstalled: _host.IsVariantInstalled(Family, size, build));
                // A verdict may already exist from the background evaluation
                // (switching sizes and back rebuilds rows from scratch).
                if (!option.IsInstalled && _fitVerdicts.TryGetValue(build, out var verdict))
                {
                    option.FitsOnDevice = verdict.Fits;
                    option.FitNote = verdict.Note;
                }
                Variants.Add(option);
                first = false;
            }
            OnPropertyChanged(nameof(HasVariants));
        }

        // ---- Fit evaluation ----

        /// <summary>
        /// Probes every build of every size (sequentially — the probe is
        /// cheap: a cached device list plus pure estimator math) and dims the
        /// rows a build doesn't fit: variant rows individually, size cells
        /// when NONE of their builds fits (the same any-build-fits rule the
        /// browse list uses for families). Verdicts are cached per build so a
        /// size switch rebuilds rows with the verdicts already applied. A
        /// failed probe counts as "unknown" and never dims (fail open),
        /// matching every other fit surface in the app.
        /// </summary>
        private async Task EvaluateAllFitsAsync(CancellationToken token)
        {
            try
            {
                await EvaluateAllFitsCoreAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Details closed mid-evaluation — the rows keep whatever
                // verdicts already landed; a fresh open re-evaluates.
            }
        }

        private async Task EvaluateAllFitsCoreAsync(CancellationToken token)
        {
            foreach (var sizeOption in Sizes.ToList())
            {
                var size = sizeOption.Size;
                var anyFits = false;
                var anyUnknown = false;
                string? note = null;
                var noteBytes = ulong.MaxValue;

                foreach (var build in size.Builds)
                {
                    token.ThrowIfCancellationRequested();

                    VariantFitVerdict verdict;
                    try
                    {
                        verdict = await _variantFitProbe!(size, build, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Probe failed for this build — unknown, not "doesn't fit".
                        anyUnknown = true;
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    _fitVerdicts[build] = verdict;

                    if (verdict.Fits)
                    {
                        anyFits = true;
                    }
                    else if (build.SizeBytes < noteBytes)
                    {
                        // The note quotes the least-demanding build that still
                        // didn't fit — the size's best shot (list-row parity).
                        note = verdict.Note;
                        noteBytes = build.SizeBytes;
                    }

                    // Live-update the row when this size is on screen.
                    var visible = Variants.FirstOrDefault(v => v.Build == build);
                    if (visible is { IsInstalled: false })
                    {
                        visible.FitsOnDevice = verdict.Fits;
                        visible.FitNote = verdict.Note;
                    }
                }

                sizeOption.FitsOnDevice = anyFits || anyUnknown || size.Builds.Count == 0;
                sizeOption.FitNote = sizeOption.FitsOnDevice ? null : note;
            }
        }

        /// <summary>Cancels the in-flight fit probes (closing the details view).</summary>
        public void Dispose()
        {
            _fitCts?.Cancel();
            _fitCts = null;
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
