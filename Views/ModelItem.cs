using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LlamaApp.Views;

/// <summary>
/// Lightweight view-model item for a model row. Represents either a locally
/// downloaded GGUF model or a Hugging Face Hub model available for download.
/// </summary>
public sealed class ModelItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Short display name: the part of <see cref="Name"/> after the last '/'.</summary>
    public string DisplayName => Name.Split('/', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
    public string Parameters { get; set; } = "";
    public string Size { get; set; } = "";
    public string License { get; set; } = "";
    public ImageSource? Logo { get; set; }
    /// <summary>True for Hub models that can be downloaded; false for locally available models (run/play).</summary>
    public bool Downloadable { get; set; }

    /// <summary>
    /// Brand/family label from the catalog (e.g. "Qwen", "OpenAI", "Mistral").
    /// When set, drives <see cref="Logo"/> via <see cref="ResolveLogo"/> — so
    /// rows show the brand's logo from Assets/Logos rather than a placeholder.
    /// </summary>
    public string? Brand { get; set; }

    /// <summary>
    /// Resolves a <see cref="Brand"/> to a bundled brand-logo ImageSource
    /// (Assets/Logos/&lt;logo&gt;.svg), using the brand→logo mapping. Returns
    /// null when the brand is unknown — the XAML Image then stays empty and the
    /// row shows the background tile alone.
    /// </summary>
    /// <remarks>
    /// The mapping is the same the macOS app uses for its
    /// <c>brandLogoAsset</c> — keys are matched case-insensitively and by
    /// prefix (so "Ministral" → "mistral", "GPT-OSS" → "gpt", etc.).
    /// </remarks>
    public static ImageSource? ResolveLogo(string? brand)
    {
        var logo = BrandToLogo(brand);
        if (logo is null) return null;

        // ms-appx:/// resolves to the app's base directory; Assets are copied
        // next to the executable (Content items in the .csproj). SVG files
        // require SvgImageSource — BitmapImage only handles raster formats.
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