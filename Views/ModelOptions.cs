namespace LlamaApp.Views;

/// <summary>
/// One downloadable build of a model — a quantization variant with its
/// download size. Listed in a Recommended row's quantization picker; the
/// selected build determines which <c>repo:quant</c> the server fetches.
/// </summary>
public sealed class QuantOption
{
    // No 'required' members: the XAML compiler's generated type info
    // instantiates these with a parameterless constructor.

    /// <summary>Quantization label, e.g. "Q4_K_M", "mxfp4" (the server id suffix).</summary>
    public string Quant { get; init; } = "";

    /// <summary>Formatted download size from the catalog, e.g. "11 GB" ("" when unknown).</summary>
    public string SizeLabel { get; init; } = "";

    /// <summary>Raw download size in bytes (0 when unknown) — feeds the disk-space preflight.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Picker label, e.g. "Q4_K_M · 11 GB" — the size belongs in the
    /// choice: quants differ mainly by quality-per-GB.</summary>
    public string Label => string.IsNullOrWhiteSpace(SizeLabel) ? Quant : $"{Quant} · {SizeLabel}";

    /// <summary>ComboBox fallback display when DisplayMemberPath isn't applied.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// One entry of a Recommended row's context-size picker. Context size is a
/// load-time parameter: it reaches the server as a <c>ctx-size</c> section in
/// the model-presets INI (see <see cref="Llama.LlamaManager.SetModelContextSizes"/>).
/// </summary>
public sealed class ContextSizeOption
{
    /// <summary>Picker label, e.g. "32K".</summary>
    public string Label { get; init; } = "";

    /// <summary>Context length in tokens; 0 = the model's own trained context
    /// (the server's default when no preset applies).</summary>
    public int CtxSize { get; init; }

    /// <summary>ComboBox fallback display when DisplayMemberPath isn't applied.</summary>
    public override string ToString() => Label;
}
