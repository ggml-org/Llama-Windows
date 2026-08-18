using System.Text.RegularExpressions;

namespace LlamaApp.Llama;

/// <summary>
/// An estimated memory footprint of a model when loaded by llama.cpp:
/// the weights, the KV cache for the target context, and a compute/alloc
/// overhead. All values in bytes.
/// </summary>
public sealed record MemoryEstimate
{
    /// <summary>Weight bytes — from the GGUF file size when known (most
    /// accurate), otherwise derived from parameter count × bits/weight.</summary>
    public ulong WeightBytes { get; init; }

    /// <summary>Estimated KV cache bytes for <see cref="ContextSize"/>
    /// (0 when the parameter count is unknown — see
    /// <see cref="ModelMemoryEstimator.EstimateKvCacheBytes"/>).</summary>
    public ulong KvCacheBytes { get; init; }

    /// <summary>Compute-graph / allocation overhead added on top of the
    /// weights (llama.cpp needs scratch buffers beyond the mapped model).</summary>
    public ulong OverheadBytes { get; init; }

    /// <summary>The context size the KV cache estimate was computed for.</summary>
    public int ContextSize { get; init; }

    /// <summary>Total bytes the model is expected to occupy when loaded.</summary>
    public ulong TotalBytes => WeightBytes + KvCacheBytes + OverheadBytes;

    /// <summary>True when nothing could be estimated (no file size, no
    /// parseable parameter count) — callers treat this as "unknown" and
    /// fail open rather than blocking on a guess they don't have.</summary>
    public bool IsUnknown => WeightBytes == 0;
}

/// <summary>
/// Pure estimation math for deciding whether a model can fit on this
/// machine's devices — from the catalog metadata alone, before anything is
/// downloaded or loaded: the parameter count (<c>"20B"</c>), the quant
/// (<c>"Q4_K_M"</c>), and — when known — the GGUF file size, which is the
/// best proxy for the in-memory weight size. Kept static and side-effect-free
/// so it stays unit-testable, mirroring <c>ServerStatusPresentation</c>.
/// </summary>
public static partial class ModelMemoryEstimator
{
    /// <summary>
    /// Context size the KV cache estimate assumes — the llama-server default
    /// (<c>--ctx-size 4096</c>), i.e. what a model will actually allocate
    /// when the app loads it without overriding the flag.
    /// </summary>
    public const int DefaultContextSize = 4096;

    /// <summary>
    /// Lower bound on the compute overhead, in bytes — even a tiny model
    /// needs real scratch buffers for graph evaluation.
    /// </summary>
    private static readonly ulong MinOverheadBytes = 256UL << 20; // 256 MiB

    /// <summary>
    /// Estimates the memory a model occupies when loaded.
    ///
    /// <para><b>Weights</b>: the on-disk GGUF size is used when
    /// <paramref name="fileSizeBytes"/> is known — the loaded weights are
    /// essentially the mapped file. Otherwise they are derived from
    /// <paramref name="parameterCount"/> × bits/weight for
    /// <paramref name="quant"/> (a coarser guess, e.g. for catalog entries
    /// missing a size).</para>
    ///
    /// <para><b>KV cache</b>: a heuristic over the parameter count for
    /// <paramref name="contextSize"/> (see
    /// <see cref="EstimateKvCacheBytes"/>) — independent of the file size,
    /// since even an exact weight size says nothing about the architecture.</para>
    ///
    /// <para><b>Overhead</b>: 10% of the weights, floored at 256 MiB.</para>
    ///
    /// Returns an <see cref="MemoryEstimate.IsUnknown"/> estimate (all zeros)
    /// when neither a file size nor a parseable parameter count is available.
    /// </summary>
    public static MemoryEstimate Estimate(
        string? parameterCount, string? quant, ulong fileSizeBytes,
        int contextSize = DefaultContextSize)
    {
        // Weights scale with the TOTAL count (memory holds every expert); the
        // KV cache scales with the ACTIVE count — see ParseActiveParameterCount.
        var parameters = ParseParameterCount(parameterCount);
        var activeParameters = ParseActiveParameterCount(parameterCount);

        ulong weights;
        if (fileSizeBytes > 0)
        {
            weights = fileSizeBytes;
        }
        else
        {
            var bits = BitsPerWeight(quant);
            weights = parameters > 0 && bits is not null
                ? (ulong)(parameters * bits.Value / 8.0)
                : 0;
        }

        var kv = EstimateKvCacheBytes(activeParameters, contextSize);
        var overhead = weights == 0
            ? 0
            : Math.Max(weights / 10, MinOverheadBytes);

        return new MemoryEstimate
        {
            WeightBytes = weights,
            KvCacheBytes = kv,
            OverheadBytes = overhead,
            ContextSize = contextSize,
        };
    }

    /// <summary>
    /// Parses a catalog parameter-count label into a raw number of
    /// parameters: <c>"20B"</c> → 20e9, <c>"270M"</c> → 270e6,
    /// <c>"0.8B"</c> → 0.8e9. Special forms handled:
    /// <list type="bullet">
    /// <item>MoE labels like <c>"26B-A4B"</c> → the TOTAL count (26e9): the
    /// active count (A4B) governs speed, but memory must hold every expert.</item>
    /// <item>Effective-size labels like gemma-3n's <c>"E4B"</c> → the number
    /// as given (4e9) — that's what the weights actually occupy.</item>
    /// <item>Decorated labels like <c>"3B Reasoning"</c> → the leading number.</item>
    /// </list>
    /// Returns 0 when no number is extractable (e.g. <c>"Flash"</c>) —
    /// callers fall back to other signals rather than guessing.
    ///
    /// <para>This is the count to size <b>weights</b> with; the KV cache is
    /// sized from <see cref="ParseActiveParameterCount"/> instead.</para>
    /// </summary>
    public static long ParseParameterCount(string? parameterCount)
    {
        if (string.IsNullOrWhiteSpace(parameterCount))
            return 0;

        var match = ParameterRegex().Match(parameterCount);
        if (!match.Success)
            return 0;

        if (!double.TryParse(match.Groups["num"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return 0;

        var factor = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1_000_000_000L,
            "M" => 1_000_000L,
            "K" => 1_000L,
            _ => 0L,
        };
        return factor == 0 ? 0 : (long)(number * factor);
    }

    /// <summary>The first number-with-size-unit token in a params label.</summary>
    [GeneratedRegex(@"(?<num>\d+(?:\.\d+)?)\s*(?<unit>[BMK])\b", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterRegex();

    /// <summary>
    /// The parameter count the KV cache scales with: for MoE labels like
    /// <c>"26B-A4B"</c> the <b>active</b> count (4e9) — experts duplicate the
    /// FFN, not the attention: KV state only grows with the dense attention
    /// layers, which track the active size. Battle-tested against real MoE
    /// architectures: sizing KV from the total count inflates it 2.4–2.8×
    /// (Mixtral, Qwen3-A3B, GPT-OSS); the active count lands within ~30%.
    /// Non-MoE labels have no <c>-A</c> part and simply return
    /// <see cref="ParseParameterCount"/>. gemma-3n-style <c>"E4B"</c> labels
    /// are already the effective (active) count.
    /// </summary>
    public static long ParseActiveParameterCount(string? parameterCount)
    {
        if (string.IsNullOrWhiteSpace(parameterCount))
            return 0;

        var match = ActiveParameterRegex().Match(parameterCount);
        if (!match.Success)
            return ParseParameterCount(parameterCount);

        if (!double.TryParse(match.Groups["num"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return ParseParameterCount(parameterCount);

        var factor = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1_000_000_000L,
            "M" => 1_000_000L,
            "K" => 1_000L,
            _ => 0L,
        };
        return factor == 0 ? ParseParameterCount(parameterCount) : (long)(number * factor);
    }

    /// <summary>The <c>-A&lt;count&gt;</c> part of an MoE params label.</summary>
    [GeneratedRegex(@"-A(?<num>\d+(?:\.\d+)?)(?<unit>[BMK])\b", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveParameterRegex();

    /// <summary>
    /// Average bits per weight for a GGUF quant label (K-quants are block
    /// codes whose true average includes scales — the values below are the
    /// standard per-block averages). <c>null</c> for an unknown/empty label;
    /// the caller then relies on the file size instead of assuming a rate.
    /// </summary>
    public static double? BitsPerWeight(string? quant)
    {
        if (string.IsNullOrWhiteSpace(quant))
            return null;

        return quant.Trim().ToUpperInvariant() switch
        {
            "F32" => 32.0,
            "F16" or "BF16" => 16.0,
            "Q8_0" => 8.5,
            "Q6_K" => 6.5625,
            "Q5_K_M" => 5.68,
            "Q5_K_S" => 5.54,
            "Q5_0" => 5.5,
            "Q5_1" => 6.0,
            "Q4_K_M" => 4.85,
            "Q4_K_S" => 4.58,
            "Q4_0" => 4.5,
            "Q4_1" => 5.0,
            "Q3_K_L" => 4.27,
            "Q3_K_M" => 3.91,
            "Q3_K_S" => 3.5,
            "Q2_K" => 2.56,
            "IQ4_NL" => 4.5,
            "IQ4_XS" => 4.25,
            "IQ3_M" => 3.66,
            "IQ3_S" => 3.44,
            "IQ3_XS" => 3.3,
            "IQ2_S" => 2.5,
            "IQ2_XS" => 2.31,
            "IQ2_XXS" => 2.06,
            "IQ1_M" => 1.75,
            "IQ1_S" => 1.56,
            // MXFP4: 4-bit weights + an E8M0 scale every 32 weights.
            "MXFP4" => 4.25,
            _ => null,
        };
    }

    /// <summary>
    /// Heuristic KV-cache estimate: <c>ctx × √params × 1.5 bytes</c> (f16
    /// KV). The square root tracks how width × depth (and KV head counts)
    /// grow with model size. Battle-tested against a corpus of ~25 real
    /// architectures (Llama 2/3/3.1/3.2, Mistral, Mixtral, Gemma 2/3, Qwen
    /// 2.5/3, Phi-3, GPT-OSS — see <c>MemoryHeuristicBattleTests</c>, which
    /// pins these envelopes):
    /// <list type="bullet">
    /// <item>Modern GQA models (KV heads ≤ 16 — everything in this app's
    /// catalog): within <b>0.48×–2.83×</b> of the exact per-token KV
    /// (<c>4 × n_layers × n_kv_heads × head_dim</c> bytes).</item>
    /// <item>Legacy <b>MHA</b> models (Llama-2 7B/13B, Phi-3) are the only
    /// ones underestimated beyond 0.45× (down to ~0.21×): their KV heads
    /// equal the query heads, so KV grows faster than √params. They are not
    /// in the catalog, and the weight estimate still dominates the total.</item>
    /// <item>Overestimates (the safe direction for a fit check) top out at
    /// ~2.8× on unusually KV-frugal designs (Gemma-3 4B's single KV head).</item>
    /// </list>
    /// Pass the <see cref="ParseActiveParameterCount">active count</see> for
    /// MoE models. Returns 0 when the parameter count is unknown — an
    /// unknown KV is safer than an invented one, and the weight size still
    /// dominates.
    /// </summary>
    public static ulong EstimateKvCacheBytes(long parameterCount, int contextSize = DefaultContextSize)
    {
        if (parameterCount <= 0 || contextSize <= 0)
            return 0;

        var bytes = contextSize * Math.Sqrt(parameterCount) * 1.5;
        return (ulong)bytes;
    }
}
