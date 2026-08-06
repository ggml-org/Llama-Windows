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
    /// The row's tooltip: the full (possibly ellipsized) name, plus the
    /// catalog's one-line <see cref="Description"/> on a second line when
    /// known — so you can tell what a model is before downloading it.
    /// </summary>
    public string RowToolTip => string.IsNullOrWhiteSpace(Description)
        ? Name
        : $"{Name}\n{Description}";

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
            OnPropertyChanged(nameof(ProgressRingVisible));
            OnPropertyChanged(nameof(LoadingRingVisible));
            OnPropertyChanged(nameof(OpenGlyphVisible));
            OnPropertyChanged(nameof(IsIndeterminateDownload));
            OnPropertyChanged(nameof(DownloadPercentTextVisible));
            OnPropertyChanged(nameof(CancelDownloadVisible));
            OnPropertyChanged(nameof(SubtitleText));
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
            OnPropertyChanged(nameof(IsIndeterminateDownload));
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
            OnPropertyChanged(nameof(LoadingRingVisible));
            OnPropertyChanged(nameof(OpenGlyphVisible));
            OnPropertyChanged(nameof(IsIndeterminateLoad));
            OnPropertyChanged(nameof(LoadPercentTextVisible));
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
            OnPropertyChanged(nameof(LoadingRingVisible));
            OnPropertyChanged(nameof(OpenGlyphVisible));
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
    public bool PlayGlyphVisible => !IsDownloading && !IsLoading && !IsLoaded && !DownloadFailed && !LoadFailed;

    /// <summary>True when the download progress ring should be visible.</summary>
    public bool ProgressRingVisible => IsDownloading;

    /// <summary>
    /// True when the row's cancel-download button should be visible — while an
    /// app-driven download is in flight. Externally-triggered downloads (WebUI /
    /// CLI) have no <see cref="DownloadCancellation"/> source, so the button
    /// hides rather than offering a no-op cancel.
    /// </summary>
    public bool CancelDownloadVisible => IsDownloading && DownloadCancellation is not null;

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
    /// live progress detail (<see cref="DownloadDetailText"/>); otherwise the
    /// catalog's "params · size" pair (empty parts dropped).
    /// </summary>
    public string SubtitleText => IsDownloading && DownloadTotalBytes > 0
        ? DownloadDetailText
        : string.Join(" · ", new[] { Parameters, Size }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

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