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
    private bool _downloadFailed;

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

    public string Parameters { get; set; } = "";
    public string Size { get; set; } = "";
    public string License { get; set; } = "";
    public bool Vision { get; set; }
    /// <summary>Quantization label, e.g. "Q4_0", "mxfp4" (used for ServerModelId).</summary>
    public string Quant { get; set; } = "";
    public ImageSource? Logo { get; set; }
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
            if (_isDownloading != value)
            {
                _isDownloading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlayGlyphVisible));
                OnPropertyChanged(nameof(ProgressRingVisible));
                OnPropertyChanged(nameof(IsIndeterminateDownload));
            }
        }
    }

    /// <summary>Download completion fraction (0..1); bound to the progress ring.</summary>
    public double DownloadFraction
    {
        get => _downloadFraction;
        set
        {
            if (Math.Abs(_downloadFraction - value) > 0.001)
            {
                _downloadFraction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DownloadProgressPercent));
                OnPropertyChanged(nameof(IsIndeterminateDownload));
            }
        }
    }

    /// <summary>True if the download failed; the row shows an error indicator.</summary>
    public bool DownloadFailed
    {
        get => _downloadFailed;
        set { if (_downloadFailed != value) { _downloadFailed = value; OnPropertyChanged(); } }
    }

    // ---- Derived UI state (avoids needing XAML value converters) ----

    /// <summary>True when the play glyph should be visible (not downloading).</summary>
    public bool PlayGlyphVisible => !IsDownloading;

    /// <summary>True when the progress ring should be visible (downloading).</summary>
    public bool ProgressRingVisible => IsDownloading;

    /// <summary>True when the ring should spin indeterminately (no bytes yet).</summary>
    public bool IsIndeterminateDownload => IsDownloading && DownloadFraction <= 0;

    /// <summary>Download completion as a percentage (0..100) for ProgressRing.Value.</summary>
    public double DownloadProgressPercent => DownloadFraction * 100;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    /// <summary>
    /// Resolves a <see cref="Brand"/> to a bundled brand-logo ImageSource
    /// (Assets/Logos/&lt;logo&gt;.svg), using the brand→logo mapping. Returns
    /// null when the brand is unknown — the XAML Image then stays empty and the
    /// row shows the background tile alone.
    /// </summary>
    public static ImageSource? ResolveLogo(string? brand)
    {
        var logo = BrandToLogo(brand);
        if (logo is null) return null;

        return new SvgImageSource(new Uri($"ms-appx:///Assets/Logos/{logo}.svg"));
    }

    /// <summary>
    /// Brand → logo filename mapping (case-insensitive prefix match). Mirrors
    /// the macOS app's brandLogoAsset table. Returns null for unknown brands.
    /// </summary>
    private static string? BrandToLogo(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand)) return null;
        var b = brand.Trim();

        bool Has(string key) => b.StartsWith(key, StringComparison.OrdinalIgnoreCase);

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
    }
}