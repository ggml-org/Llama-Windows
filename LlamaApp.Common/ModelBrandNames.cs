using System.Text.RegularExpressions;

namespace LlamaApp.Common;

/// <summary>
/// Derives a model-family brand from a model's NAME when no catalog entry
/// exists for it (an imported .gguf, a folder-enrolled model, a Hub repo
/// outside the catalog) — so those models still get their family's logo
/// instead of an empty tile. Known family keywords match inside the name
/// (<c>gemma-3-1b-it-qat-Q4_0</c> → <c>gemma</c>, <c>Qwen3.8-27B</c> →
/// <c>qwen</c>); the brands are the ones the bundled logos cover (mirrors the
/// app's brand→logo mapping and the macOS app's table).
///
/// <para>Pure — the mapping rules (including the GPTQ lookalike guard) are
/// unit-testable without a UI runtime.</para>
/// </summary>
public static class ModelBrandNames
{
    /// <summary>
    /// Family keywords → brand. Word-boundary anchored, case-insensitive;
    /// <c>gpt</c> specifically rejects GPTQ (the quantization format, not
    /// the family). Order mirrors the app's brand→logo table.
    /// </summary>
    private static readonly (Regex Pattern, string Brand)[] Patterns =
    [
        (new(@"\bqwen", RegexOptions.IgnoreCase | RegexOptions.Compiled), "qwen"),
        (new(@"\bgemma", RegexOptions.IgnoreCase | RegexOptions.Compiled), "gemma"),
        (new(@"\bopenai", RegexOptions.IgnoreCase | RegexOptions.Compiled), "gpt"),
        (new(@"\bgpt(?!q)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "gpt"),
        (new(@"\bministral|\bdevstral|\bmagistral|\bmistral", RegexOptions.IgnoreCase | RegexOptions.Compiled), "mistral"),
        (new(@"\bglm", RegexOptions.IgnoreCase | RegexOptions.Compiled), "z"),
        (new(@"\bnemotron|\bnvidia", RegexOptions.IgnoreCase | RegexOptions.Compiled), "nvidia"),
    ];

    /// <summary>
    /// The brand whose logo fits <paramref name="name"/>, or null when the
    /// name matches no known family (the caller falls back to the author
    /// avatar / the generic model icon).
    /// </summary>
    public static string? BrandFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var (pattern, brand) in Patterns)
            if (pattern.IsMatch(name))
                return brand;
        return null;
    }
}
