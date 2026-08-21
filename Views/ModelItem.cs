using System.ComponentModel;
using System.Runtime.CompilerServices;
using LlamaApp.Common;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LlamaApp.Views;

/// <summary>
/// Lightweight view-model item for a model row. Represents either a locally
/// downloaded GGUF model or a Hugging Face Hub model available for download.
/// Implements <see cref="INotifyPropertyChanged"/> so the UI can react to
/// download-progress updates in real time.
/// </summary>
public sealed class ModelItem : IModel, INotifyPropertyChanged
{
    private bool _isDownloading;
    private double _downloadFraction;
    private long _downloadedBytes;
    private long _downloadTotalBytes;
    private double _downloadBytesPerSecond;
    private bool _downloadFailed;
    private bool _downloadPaused;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isLoading;
    private double _loadFraction;
    private bool _isLoaded;
    private bool _loadFailed;
    private ImageSource? _logo;

    /// <summary>Display label (e.g. "GPT-OSS 20B (mxfp4)").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The actual Hugging Face repo id (e.g. "ggml-org/gpt-oss-20b-GGUF") used
    /// for server API calls. Separated from <see cref="Name"/> (the display
    /// label) so the UI shows a friendly name while the server gets the repo id.
    /// </summary>
    public string? RepoName { get; set; }

    // ---- IModel (explicit Name so IModel.Name returns the repo id) ----

    string IModel.Name => RepoName ?? Name;

    /// <summary>
    /// Server model id = <c>&lt;repo&gt;:&lt;quant&gt;</c> (or just <c>&lt;repo&gt;</c>
    /// when <see cref="Quant"/> is empty) — the form the llama server's
    /// <c>/models/load</c> requires. See <see cref="IModel.ServerModelId"/>.
    /// </summary>
    string IModel.ServerModelId => string.IsNullOrEmpty(Quant)
        ? (RepoName ?? Name)
        : $"{RepoName ?? Name}:{Quant}";

    public string Description { get; set; } = "";

    /// <summary>Short display name: the part of <see cref="Name"/> after the last '/'.</summary>
    public string DisplayName => Name.Split('/', StringSplitOptions.RemoveEmptyEntries).Last().Trim();

    /// <summary>
    /// <see cref="DisplayName"/> without the tag-carried parts: the trailing
    /// "(quant)" suffix and the standalone parameter-count token. Rows (and
    /// the details header) show this clean name — the quant rides in the
    /// chip next to it, the params in the subtitle/chip — so multi-quant
    /// repos stay distinguishable without stuffing everything into the name.
    /// </summary>
    public string RowDisplayName
    {
        get
        {
            var name = DisplayName;
            if (!string.IsNullOrEmpty(Quant) &&
                name.EndsWith("(" + Quant + ")", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^(Quant.Length + 2)].TrimEnd();
            }
            if (!string.IsNullOrWhiteSpace(Parameters))
            {
                // The params phrase is stripped only at a space boundary
                // ("Gemma 3 1B" → "Gemma 3", "Ministral 3 3B Reasoning" →
                // "Ministral 3"); inside a hyphenated slug
                // ("Ministral-3-3B-Reasoning") it is part of the name and stays.
                var idx = name.IndexOf(Parameters, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    var startOk = idx == 0 || name[idx - 1] == ' ';
                    var end = idx + Parameters.Length;
                    var endOk = end == name.Length || name[end] == ' ';
                    if (startOk && endOk)
                    {
                        var stripped = string.Join(' ',
                            name.Remove(idx, Parameters.Length)
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        if (stripped.Length > 0) name = stripped;
                        break;
                    }
                    idx = name.IndexOf(Parameters, idx + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            return name;
        }
    }

    /// <summary>True when a quant label exists — drives the row's quant chip.</summary>
    public bool HasQuant => !string.IsNullOrWhiteSpace(Quant);

    /// <summary>
    /// The row's tooltip: the full (possibly ellipsized) name, the catalog's
    /// one-line <see cref="Description"/> on a second line when known — so
    /// you can tell what a model is before downloading it — and the
    /// <see cref="FitNote"/> on a last line when the fit evaluation flagged
    /// the row as too big for this machine. Notifies (via
    /// <see cref="FitsOnDevice"/>/<see cref="FitNote"/>) because the fit
    /// evaluation lands after the rows are rendered.
    /// </summary>
    public string RowToolTip
    {
        get
        {
            var head = string.IsNullOrWhiteSpace(Description)
                ? Name
                : $"{Name}\n{Description}";
            return string.IsNullOrWhiteSpace(FitNote)
                ? head
                : $"{head}\n{FitNote}";
        }
    }

    public string Parameters { get; set; } = "";
    public string Size { get; set; } = "";

    /// <summary>
    /// Raw download size in bytes from the catalog (0 when unknown) — used by
    /// the disk-space preflight before a Recommended-row download starts.
    /// </summary>
    public ulong SizeBytes { get; set; }
    public string License { get; set; } = "";
    public bool Vision { get; set; }

    /// <summary>Quantization label, e.g. "Q4_0", "mxfp4" (used for ServerModelId).</summary>
    public string? Quant { get; set; } = null;

    // ---- Device-fit state (dims Recommended rows the machine can't run) ----

    private bool _fitsOnDevice = true;
    private string? _fitNote;

    /// <summary>
    /// Whether this machine can run the model — set by the fit evaluation
    /// (MainWindow.EvaluateRecommendedFitsAsync) from the
    /// <c>llama --list-devices</c> probe plus system RAM, after the rows are
    /// rendered. Defaults to <c>true</c>: an unknown machine size must never
    /// dim a row (fail open, same as the download preflight). A
    /// <c>false</c> row renders dimmed (<see cref="RowOpacity"/>) but stays
    /// clickable — the preflight still has the final say on click.
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
    /// Short reason the model doesn't fit (e.g. "May not fit: needs about
    /// 14.2 GB…"), surfaced as the tooltip's last line; <c>null</c> while it
    /// fits or the evaluation hasn't run.
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

    /// <summary>
    /// Row opacity: a Recommended row the fit evaluation says the machine
    /// can't run renders dimmed instead of full-strength.
    /// </summary>
    public double RowOpacity => FitsOnDevice ? 1.0 : 0.4;
    /// <summary>
    /// The resolved brand logo (theme-dependent — see <see cref="ResolveLogo"/>).
    /// Notifies so the shell can re-resolve logos on a theme change.
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
    /// <summary>True for Hub models that can be downloaded; false for locally available models (run/play).</summary>
    public bool Downloadable { get; set; }
    public string? Brand { get; set; }

    // ---- Download progress state (drives the progress ring) ----

    /// <summary>True while a download is in flight; the row shows a progress ring.</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value) return;
                
            _isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayGlyphVisible));
            OnPropertyChanged(nameof(DeleteGlyphVisible));
            OnPropertyChanged(nameof(ProgressRingVisible));
            OnPropertyChanged(nameof(LoadingRingVisible));
            OnPropertyChanged(nameof(OpenGlyphVisible));
            OnPropertyChanged(nameof(IsIndeterminateDownload));
            OnPropertyChanged(nameof(DownloadPercentTextVisible));
            OnPropertyChanged(nameof(CancelDownloadVisible));
            OnPropertyChanged(nameof(CanPauseDownload));
            OnPropertyChanged(nameof(ResumeDownloadVisible));
            OnPropertyChanged(nameof(PausedPercentTextVisible));
            OnPropertyChanged(nameof(SubtitleText));
            NotifyAccessibleNameChanged();
        }
    }

    /// <summary>Download completion fraction (0..1); bound to the progress ring.</summary>
    public double DownloadFraction
    {
        get => _downloadFraction;
        set
        {
            if (!(Math.Abs(_downloadFraction - value) > 0.001)) return;
            
            _downloadFraction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadProgressPercent));
            OnPropertyChanged(nameof(DownloadProgressText));
            OnPropertyChanged(nameof(DownloadPercentTextVisible));
            OnPropertyChanged(nameof(PausedPercentTextVisible));
            OnPropertyChanged(nameof(IsIndeterminateDownload));
            NotifyAccessibleNameChanged(); // the accessible name carries the percent
        }
    }

    /// <summary>
    /// Cancels an in-flight download. Created by the download driver
    /// (MainWindow.DownloadAndLaunchAsync) when the download starts and cleared
    /// when it ends; the row's cancel button calls
    /// <see cref="CancellationTokenSource.Cancel"/> on it. Stays null for
    /// downloads the app didn't start (WebUI / CLI) — those can't be canceled
    /// from the row, so the setter notifies <see cref="CancelDownloadVisible"/>
    /// to keep the cancel button in sync.
    /// </summary>
    public CancellationTokenSource? DownloadCancellation
    {
        get => _downloadCancellation;
        set
        {
            if (ReferenceEquals(_downloadCancellation, value)) return;
            _downloadCancellation = value;
            OnPropertyChanged(nameof(CancelDownloadVisible));
            OnPropertyChanged(nameof(CanPauseDownload));
        }
    }

    /// <summary>
    /// True when the user paused the download by clicking the progress ring.
    /// The server-side download is already aborted (pause reuses the cancel
    /// path — the partial bytes stay in the cache and the server resumes them
    /// on the next attempt), so the row swaps the ring for a resume glyph in
    /// the same slot; clicking it restarts the download. Set by the row's
    /// pause button before the cancellation unwinds; cleared by the download
    /// driver on (re)start, completion, or failure.
    /// </summary>
    public bool DownloadPaused
    {
        get => _downloadPaused;
        set
        {
            if (_downloadPaused == value) return;
            _downloadPaused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayGlyphVisible));
            OnPropertyChanged(nameof(DeleteGlyphVisible));
            OnPropertyChanged(nameof(ResumeDownloadVisible));
            OnPropertyChanged(nameof(CancelDownloadVisible));
            OnPropertyChanged(nameof(PausedPercentTextVisible));
            OnPropertyChanged(nameof(SubtitleText));
            NotifyAccessibleNameChanged();
        }
    }

    /// <summary>
    /// Bytes fetched so far, fed by the download driver (and the external-
    /// download watch) from the server's SSE progress events. Drives the
    /// detail line that replaces the subtitle while downloading.
    /// </summary>
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (_downloadedBytes == value) return;
            _downloadedBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadDetailText));
            OnPropertyChanged(nameof(SubtitleText));
            NotifyAccessibleNameChanged(); // the accessible name carries the percent
        }
    }

    /// <summary>Total bytes to fetch; 0 until the server reports a size.</summary>
    public long DownloadTotalBytes
    {
        get => _downloadTotalBytes;
        set
        {
            if (_downloadTotalBytes == value) return;
            _downloadTotalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadDetailText));
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    /// <summary>
    /// Smoothed download rate in bytes/sec, estimated by the download driver
    /// between throttled progress samples; 0 until the first estimate.
    /// </summary>
    public double DownloadBytesPerSecond
    {
        get => _downloadBytesPerSecond;
        set
        {
            if (Math.Abs(_downloadBytesPerSecond - value) < 1.0) return;
            _downloadBytesPerSecond = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadDetailText));
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    /// <summary>True if the download failed; the row shows an error indicator.</summary>
    public bool DownloadFailed
    {
        get => _downloadFailed;
        set
        {
            if (_downloadFailed == value) return;
            _downloadFailed = value;
            OnPropertyChanged();
            // A failed row swaps the play glyph for the warning + retry affordance.
            OnPropertyChanged(nameof(PlayGlyphVisible));
            OnPropertyChanged(nameof(DeleteGlyphVisible));
            NotifyAccessibleNameChanged();
        }
    }

    /// <summary>
    /// True when the last load request was rejected by the server (OOM, corrupt
    /// GGUF, …) or never confirmed — the row shows a warning + retry affordance
    /// instead of the play glyph, mirroring <see cref="DownloadFailed"/>. Set by
    /// the load driver (MainWindow.LoadAndWatchAsync); cleared when a load is
    /// (re)attempted and when the server reports the model loaded.
    /// </summary>
    public bool LoadFailed
    {
        get => _loadFailed;
        set
        {
            if (_loadFailed == value) return;
            _loadFailed = value;
            OnPropertyChanged();
            // A failed row swaps the play glyph for the warning + retry affordance.
            OnPropertyChanged(nameof(PlayGlyphVisible));
            OnPropertyChanged(nameof(DeleteGlyphVisible));
            NotifyAccessibleNameChanged();
        }
    }

    // ---- Model load state (drives the Available-row action cell) ----
    // The lifecycle of a local model row, left to right:
    //   unloaded --(play click)--> loading --(server reports loaded)--> loaded
    // A row that's mid-download shows the download ring first, then transitions
    // to loading once the download finishes and the load request is sent.

    /// <summary>
    /// True while a load request is in flight — the row shows a load ring
    /// (indeterminate until the server's <c>status_change</c> events report a
    /// fraction via <see cref="LoadFraction"/>). Set optimistically by the
    /// play-click handler; cleared by the model-state poller once the server
    /// reports the model as <c>loaded</c> (or by the load caller on rejection).
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading == value) return;
            
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayGlyphVisible));
            OnPropertyChanged(nameof(DeleteGlyphVisible));
            OnPropertyChanged(nameof(LoadingRingVisible));
            OnPropertyChanged(nameof(OpenGlyphVisible));
            OnPropertyChanged(nameof(IsIndeterminateLoad));
            OnPropertyChanged(nameof(LoadPercentTextVisible));
            NotifyAccessibleNameChanged();
        }
    }

    /// <summary>
    /// Load completion fraction (0..1), fed by the server's <c>status_change</c>
    /// SSE events while the model loads; 0 until the first event arrives (the
    /// load ring spins indeterminately until then — also the steady state for
    /// externally-triggered loads, which only the poller observes).
    /// </summary>
    public double LoadFraction
    {
        get => _loadFraction;
        set
        {
            if (!(Math.Abs(_loadFraction - value) > 0.001)) return;
            
            _loadFraction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoadProgressPercent));
            OnPropertyChanged(nameof(LoadProgressText));
            OnPropertyChanged(nameof(LoadPercentTextVisible));
            OnPropertyChanged(nameof(IsIndeterminateLoad));
        }
    }

    /// <summary>
    /// True when the server reports the model as <c>loaded</c> — the row shows
    /// the OpenInNewWindow glyph (click to open the WebUI). Updated by the
    /// model-state poller.
    /// </summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        set
        {
            if (_isLoaded == value) return;
            
            _isLoaded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayGlyphVisible));
            OnPropertyChanged(nameof(DeleteGlyphVisible));
            OnPropertyChanged(nameof(LoadingRingVisible));
            OnPropertyChanged(nameof(OpenGlyphVisible));
            NotifyAccessibleNameChanged();
        }
    }

    // ---- Derived UI state (avoids needing XAML value converters) ----

    // The Available-row action cell shows exactly one of: play (unloaded),
    // download ring (downloading from the Hub), load ring (load request
    // sent), or OpenInNewWindow (loaded). Download takes
    // priority over a load (a row can't load until it's downloaded).

    /// <summary>
    /// True when the play glyph should be visible (unloaded, idle). A failed
    /// download shows the warning + retry affordance instead of play — the
    /// model isn't (fully) cached, so loading it would just be rejected. A
    /// failed load shows the same affordance so the rejection isn't silent.
    /// </summary>
    public bool PlayGlyphVisible => !IsDownloading && !IsLoading && !IsLoaded && !DownloadFailed && !LoadFailed && !DownloadPaused;

    /// <summary>
    /// Whether the server allows removing this model from the cache (the
    /// <c>can_remove</c> flag in <c>GET /models</c> — false for models sourced
    /// from a presets file or <c>--models-dir</c>, which the router refuses
    /// to delete). Fed by the poller (<c>BuildLocalItem</c>/<c>ReconcileAsync</c>).
    /// Defaults to <c>true</c>: a row the server hasn't described yet (freshly
    /// downloaded, snapshot still pending) keeps the affordance — fail open,
    /// like every other probe in the app; a wrongful delete attempt is
    /// surfaced by the failure flyout instead of being silently hidden.
    /// </summary>
    public bool CanRemove
    {
        get => _canRemove;
        set
        {
            if (_canRemove == value) return;
            _canRemove = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeleteGlyphVisible));
        }
    }
    private bool _canRemove = true;

    /// <summary>
    /// True when the row's delete (bin) affordance should be visible: an idle
    /// row the server can actually remove. Offering delete for a model the
    /// router will refuse (can_remove=false) made the bin icon a silent no-op.
    /// </summary>
    public bool DeleteGlyphVisible => PlayGlyphVisible && CanRemove;

    /// <summary>True when the download progress ring should be visible.</summary>
    public bool ProgressRingVisible => IsDownloading;

    /// <summary>
    /// True when the row's resume-download affordance should be visible — the
    /// paused state, occupying the progress ring's slot so pause/resume
    /// toggles in place. Excludes the transitional states a racing completion
    /// could otherwise leave behind (load ring up, or the failure pair).
    /// </summary>
    public bool ResumeDownloadVisible =>
        DownloadPaused && !IsDownloading && !IsLoading && !IsLoaded && !DownloadFailed;

    /// <summary>
    /// True when the progress ring's pause click can act — an app-driven
    /// download is in flight. Externally-triggered downloads (WebUI / CLI)
    /// have no <see cref="DownloadCancellation"/> source, so the ring's pause
    /// button disables rather than offering a no-op pause (same rule as the
    /// cancel button).
    /// </summary>
    public bool CanPauseDownload => IsDownloading && DownloadCancellation is not null;

    /// <summary>
    /// True when the row's cancel-download button should be visible — while an
    /// app-driven download is in flight, or while paused (there it abandons
    /// the partial download and returns the row to the play glyph).
    /// Externally-triggered downloads (WebUI / CLI) have no
    /// <see cref="DownloadCancellation"/> source, so the button hides rather
    /// than offering a no-op cancel.
    /// </summary>
    public bool CancelDownloadVisible =>
        (IsDownloading && DownloadCancellation is not null) ||
        (DownloadPaused && !IsDownloading);

    /// <summary>True when the load ring should be visible.</summary>
    public bool LoadingRingVisible => IsLoading && !IsDownloading;

    /// <summary>True when the OpenInNewWindow glyph should be visible (loaded).</summary>
    public bool OpenGlyphVisible => IsLoaded && !IsDownloading && !IsLoading;

    /// <summary>True when the ring should spin indeterminately (no bytes yet).</summary>
    public bool IsIndeterminateDownload => IsDownloading && DownloadFraction <= 0;

    /// <summary>Download completion as a percentage (0..100) for ProgressRing.Value.</summary>
    public double DownloadProgressPercent => DownloadFraction * 100;

    /// <summary>Download completion as a short label (e.g. "42%") shown under the ring.</summary>
    public string DownloadProgressText => $"{DownloadProgressPercent:0}%";

    /// <summary>
    /// True when the percent caption should be visible — while downloading with
    /// a known byte count (an indeterminate ring shows no caption).
    /// </summary>
    public bool DownloadPercentTextVisible => IsDownloading && DownloadFraction > 0;

    /// <summary>
    /// True when the frozen percent caption should show under the paused
    /// row's resume glyph — the download's last known completion, kept so the
    /// paused slot keeps the ring's shape.
    /// </summary>
    public bool PausedPercentTextVisible => DownloadPaused && !IsDownloading && DownloadFraction > 0;

    /// <summary>True when the load ring should spin indeterminately (no progress reported yet).</summary>
    public bool IsIndeterminateLoad => IsLoading && LoadFraction <= 0;

    /// <summary>Load completion as a percentage (0..100) for ProgressRing.Value.</summary>
    public double LoadProgressPercent => LoadFraction * 100;

    /// <summary>Load completion as a short label (e.g. "42%") shown under the load ring.</summary>
    public string LoadProgressText => $"{LoadProgressPercent:0}%";

    /// <summary>
    /// True when the load percent caption should be visible — while the load
    /// ring is up and a progress fraction has been reported.
    /// </summary>
    public bool LoadPercentTextVisible => LoadingRingVisible && LoadFraction > 0;

    /// <summary>
    /// The download detail line, e.g. "3.2 GB of 12.1 GB · 45 MB/s · ~4 min
    /// left" (formatting rules live in <see cref="DownloadProgressPresentation"/>).
    /// </summary>
    public string DownloadDetailText => DownloadProgressPresentation.FormatDetail(
        DownloadedBytes, DownloadTotalBytes, DownloadBytesPerSecond);

    /// <summary>
    /// The row's subtitle line: while a download with a known size runs, the
    /// live progress detail (<see cref="DownloadDetailText"/>); while paused,
    /// the frozen byte counts with a "Paused" marker; otherwise the catalog's
    /// "params · size" pair (empty parts dropped).
    /// </summary>
    public string SubtitleText => IsDownloading && DownloadTotalBytes > 0
        ? DownloadDetailText
        : DownloadPaused && DownloadTotalBytes > 0
            ? DownloadProgressPresentation.FormatPausedDetail(DownloadedBytes, DownloadTotalBytes)
            : string.Join(" · ", new[] { Parameters, Size }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

    // ---- Row state signals ----
    // The running state is a green badge pinned to the logo tile; every other
    // state already has its own affordance (load ring, warning dot + retry,
    // progress ring). Screen readers get the state from the row's accessible
    // name, so no state is color-only.

    /// <summary>
    /// The row's accessible name, e.g. "Gemma 3, ready" / "Gemma 3, running"
    /// / "Gemma 3, downloading 42%".
    /// </summary>
    public string RowAccessibleName => $"{RowDisplayName}, {AccessibleStatusText}";

    private string AccessibleStatusText =>
        IsDownloading ? (DownloadFraction > 0 ? $"downloading {DownloadProgressPercent:0}%" : "downloading")
        : DownloadPaused ? "download paused"
        : DownloadFailed || LoadFailed ? "error"
        : IsLoaded ? "running"
        : IsLoading ? "starting"
        : "ready";

    /// <summary>Re-notifies the row's accessible name (called from the state setters).</summary>
    private void NotifyAccessibleNameChanged()
        => OnPropertyChanged(nameof(RowAccessibleName));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    // Resolved-logo cache: parsing an SVG into an SvgImageSource isn't free,
    // and the same few brands repeat across every row — the Recommended list
    // alone can hold dozens of rows sharing ~6 brands, and the Available list
    // is rebuilt from scratch on every full populate. ImageSources are
    // shareable across Image elements, so one instance per brand serves all
    // rows. Callers are on the UI thread; the lock is belt-and-suspenders.
    private static readonly object LogoCacheLock = new();
    private static readonly Dictionary<string, ImageSource?> LogoCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Set by the shell (MainWindow) from the effective theme: true while the
    /// dark theme is active → <see cref="ResolveLogo"/> picks the white
    /// &quot;.light&quot; SVG variants. Read/written on the UI thread, like the cache.
    /// </summary>
    public static bool UseLightLogos;

    /// <summary>
    /// Clears the resolved-logo cache. Called by the shell on a theme change,
    /// before re-resolving every row's <see cref="Logo"/>.
    /// </summary>
    public static void ClearLogoCache()
    {
        lock (LogoCacheLock) LogoCache.Clear();
    }

    /// <summary>
    /// Resolves a <see cref="Brand"/> to a bundled brand-logo ImageSource
    /// (Assets/Logos/&lt;logo&gt;.svg), using the brand→logo mapping. Returns
    /// null when the brand is unknown — the XAML Image then stays empty, and the
    /// row shows the background tile alone. Instances are cached per brand (see
    /// <see cref="LogoCache"/>).
    ///
    /// <para>The bundled SVGs fill with <c>currentColor</c>, which
    /// <see cref="SvgImageSource"/> renders as black — invisible on the dark
    /// theme's Mica surface. When <see cref="UseLightLogos"/> is set, the
    /// white-filled &lt;logo&gt;.light.svg variant is used instead.</para>
    /// </summary>
    public static ImageSource? ResolveLogo(string? brand)
    {
        var logo = BrandToLogo(brand);
        if (logo is null) return null;

        // Dark theme → white artwork (the default variant rasterizes black).
        // The variant suffix becomes part of the cache key, so both themes'
        // instances coexist in the cache.
        if (UseLightLogos) logo += ".light";

        lock (LogoCacheLock)
        {
            if (LogoCache.TryGetValue(logo, out var cached))
                return cached;

            // Rasterize at a bounded 64px width rather than the SVG's natural
            // size: the tile renders the logo at 24px logical (48px physical at
            // 200% scale), so a small raster keeps per-image memory down while
            // staying crisp on high-DPI displays. Setting only the width
            // preserves the aspect ratio.
            var source = new SvgImageSource(new Uri($"ms-appx:///Assets/Logos/{logo}.svg"))
            {
                RasterizePixelWidth = 64,
            };
            LogoCache[logo] = source;
            return source;
        }
    }

    /// <summary>
    /// Brand → logo filename mapping (case-insensitive prefix match). Mirrors
    /// the macOS app's brandLogoAsset table. Returns null for unknown brands.
    /// </summary>
    private static string? BrandToLogo(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand)) return null;
        var b = brand.Trim();

        if (Has("qwen")) return "qwen";
        if (Has("gemma")) return "gemma";
        if (Has("openai")) return "gpt";
        if (Has("gpt")) return "gpt";
        if (Has("mistral")) return "mistral";
        if (Has("ministral")) return "mistral";
        if (Has("devstral")) return "mistral";
        if (Has("magistral")) return "mistral";
        if (Has("glm")) return "z";
        if (Has("nemotron")) return "nvidia";
        if (Has("nvidia")) return "nvidia";

        return null;

        bool Has(string key) => b.StartsWith(key, StringComparison.OrdinalIgnoreCase);
    }
}