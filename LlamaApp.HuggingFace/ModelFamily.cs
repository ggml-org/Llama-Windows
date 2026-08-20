using System.Collections.Generic;

namespace LlamaApp.HuggingFace
{
    /// <summary>
    /// A model family from the catalog (e.g. "Gemma 3") — the unit the
    /// browse list renders. Variant-independent on purpose: a family row
    /// shows the name, the available parameter sizes and a one-line
    /// description; the per-quant builds live one level down in
    /// <see cref="ModelFamilySize.Builds"/> and only surface in the family
    /// details view.
    /// </summary>
    public sealed class ModelFamily
    {
        /// <summary>Family display name from the catalog, e.g. "Gemma 3".</summary>
        public string Name { get; init; } = "";

        /// <summary>Brand/publisher label, e.g. "Google", "OpenAI" (drives the logo).</summary>
        public string Brand { get; init; } = "";

        /// <summary>One-line catalog description shown in the family row/details.</summary>
        public string Description { get; init; } = "";

        /// <summary>License label from the catalog, e.g. "Apache-2.0".</summary>
        public string License { get; init; } = "";

        /// <summary>Whether the catalog marks this family as featured.</summary>
        public bool Featured { get; init; }

        /// <summary>The family's parameter sizes in catalog order.</summary>
        public IReadOnlyList<ModelFamilySize> Sizes { get; init; } = new List<ModelFamilySize>();
    }

    /// <summary>One parameter size of a family (e.g. "4B"), with its builds.</summary>
    public sealed class ModelFamilySize
    {
        /// <summary>Size display name from the catalog, e.g. "Gemma 3 4B".</summary>
        public string Name { get; init; } = "";

        /// <summary>Human-readable parameter count, e.g. "4B".</summary>
        public string Params { get; init; } = "";

        /// <summary>Whether this size supports image input.</summary>
        public bool Vision { get; init; }

        /// <summary>The downloadable builds (one per quant) in catalog order.</summary>
        public IReadOnlyList<ModelFamilyBuild> Builds { get; init; } = new List<ModelFamilyBuild>();
    }

    /// <summary>One downloadable build of a size: a quant of a specific GGUF repo.</summary>
    public sealed class ModelFamilyBuild
    {
        /// <summary>Quantization label, e.g. "Q4_K_M", "mxfp4".</summary>
        public string Quant { get; init; } = "";

        /// <summary>Human-readable download size, e.g. "12.1 GB".</summary>
        public string Size { get; init; } = "";

        /// <summary>Raw size in bytes (0 when unknown).</summary>
        public ulong SizeBytes { get; init; }

        /// <summary>Hugging Face repo id, e.g. "ggml-org/gemma-3-4b-it-GGUF".</summary>
        public string Repo { get; init; } = "";
    }
}
