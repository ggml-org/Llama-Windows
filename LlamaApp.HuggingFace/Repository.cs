using LlamaApp.Common;

namespace LlamaApp.HuggingFace;

/// <summary>
/// A Hugging Face repository representing a single downloadable model build
/// (one quant of one size of one model family). <see cref="Name"/> is the HF
/// repo id (e.g. <c>ggml-org/gpt-oss-20b-GGUF</c>) used for downloading.
/// </summary>
public record Repository : IModel
{
    /// <summary>Hugging Face repo id, e.g. "ggml-org/gpt-oss-20b-GGUF".</summary>
    public required string Name { get; init; }

    public required string Description { get; init; }
    public required string License { get; init; }

    /// <summary>Human-readable parameter count from the catalog, e.g. "20B".</summary>
    public required string Parameters { get; init; }

    /// <summary>Human-readable download size, e.g. "12.1 GB".</summary>
    public required string Size { get; init; }

    public required bool Vision { get; init; }

    // ---- Extra catalog metadata (not part of IModel) ----

    /// <summary>Display name for the size, e.g. "GPT-OSS 20B".</summary>
    public string? DisplayName { get; init; }

    /// <summary>Brand/family label, e.g. "OpenAI", "Qwen".</summary>
    public string? Brand { get; init; }

    /// <summary>Quantization label, e.g. "Q4_0", "mxfp4".</summary>
    public string? Quant { get; init; }

    /// <summary>Raw size in bytes — useful for sorting/comparison.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Whether the catalog marks this family as featured.</summary>
    public bool Featured { get; init; }
}
