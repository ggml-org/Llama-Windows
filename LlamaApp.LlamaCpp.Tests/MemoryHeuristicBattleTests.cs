using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Battle tests for the memory-estimation heuristics — the kind of tests
/// that earn the "heuristic" its keep: instead of asserting the formula
/// against itself, they assert it against the GROUND TRUTH of ~25 real,
/// published architectures, where the exact KV-cache size is computable
/// (<c>2 tensors × 2 bytes(f16) × n_layers × n_kv_heads × head_dim</c> per
/// token), and against the real <c>llama.app</c> catalog's file sizes.
///
/// What gets pinned:
/// <list type="bullet">
/// <item>The KV heuristic's error envelope across the corpus, split by
/// attention class — GQA (everything in the catalog) vs legacy MHA.</item>
/// <item>That every serious underestimate is confined to MHA architectures
/// (a documented risk, not an accident).</item>
/// <item>The MoE refinement (KV scales with the active count — experts
/// duplicate the FFN, not the attention) and the error it prevents.</item>
/// <item>The params×bits weight fallback's envelope vs real catalog files,
/// including its known weakness (gemma-3n "E" labels).</item>
/// <item>Dilution: KV error can't move the TOTAL estimate by more than a
/// bounded fraction — weights dominate — and the fit verdict is stable
/// whenever the budget isn't razor-thin.</item>
/// </list>
/// If a future architecture family breaks an envelope, add it to the corpus
/// and re-derive the constants — don't widen an envelope silently.
/// </summary>
public class MemoryHeuristicBattleTests
{
    /// <summary>
    /// A real architecture. <see cref="ActualKvBytesPerToken"/> is the exact
    /// f16-KV footprint per context token; <see cref="HeadDim"/> is the
    /// effective KV head dimension (GPT-OSS's asymmetric 64/256 qk/v dims
    /// enter as their mean, which preserves the byte count).
    /// </summary>
    private sealed record Arch(
        string Name,
        long TotalParams,
        long? ActiveParams,
        int Layers,
        int KvHeads,
        int HeadDim)
    {
        /// <summary>MoE experts add weights, not KV — the active count tracks
        /// the dense attention layers. Dense models: active == total.</summary>
        public long KvParams => ActiveParams ?? TotalParams;

        /// <summary>Exact KV bytes per token: 2 tensors (K,V) × 2 bytes (f16)
        /// × layers × KV heads × head dim.</summary>
        public double ActualKvBytesPerToken => 4.0 * Layers * KvHeads * HeadDim;

        /// <summary>Multi-head attention (KV heads == query heads) — the
        /// legacy design whose KV grows faster than √params. 16 covers every
        /// real MHA count (32+) while keeping the widest GQA (16) on the
        /// modern side.</summary>
        public bool IsMha => KvHeads > 16;
    }

    /// <summary>
    /// The corpus — sizes from model cards/configs, all real ships. MoE rows
    /// carry the active count used by the KV heuristic.
    /// </summary>
    private static readonly Arch[] Corpus =
    [
        new("Llama 2 7B",      6_700_000_000,  null,         32, 32, 128),
        new("Llama 2 13B",    13_000_000_000,  null,         40, 40, 128),
        new("Llama 2 70B",    70_600_000_000,  null,         80,  8, 128),
        new("Llama 3.2 1B",    1_240_000_000,  null,         16,  8,  64),
        new("Llama 3.2 3B",    3_210_000_000,  null,         28,  8,  64),
        new("Llama 3 8B",      8_030_000_000,  null,         32,  8, 128),
        new("Llama 3 70B",    70_600_000_000,  null,         80,  8, 128),
        new("Llama 3.1 405B", 405_000_000_000, null,        126,  8, 128),
        new("Mistral 7B",      7_250_000_000,  null,         32,  8, 128),
        new("Mixtral 8x7B",   46_700_000_000, 12_900_000_000, 32,  8, 128),
        new("Mixtral 8x22B", 141_000_000_000, 39_000_000_000, 56,  8, 128),
        new("Qwen3 30B-A3B",  30_500_000_000,  3_300_000_000, 48,  4, 128),
        // GPT-OSS: asymmetric 64/256 qk/v head dims → effective 160.
        new("GPT-OSS 20B",    20_900_000_000,  3_600_000_000, 24,  8, 160),
        new("GPT-OSS 120B",  117_000_000_000,  5_100_000_000, 36,  8, 160),
        new("Gemma 2 2B",      2_610_000_000,  null,         26,  4, 288),
        new("Gemma 2 9B",      9_240_000_000,  null,         42,  8, 224),
        new("Gemma 2 27B",    27_200_000_000,  null,         46, 16, 128),
        new("Gemma 3 4B",      4_300_000_000,  null,         34,  1, 256),
        new("Gemma 3 12B",    12_200_000_000,  null,         48,  2, 240),
        new("Gemma 3 27B",    27_400_000_000,  null,         62,  4, 168),
        new("Qwen 2.5 7B",     7_620_000_000,  null,         28,  4, 128),
        new("Qwen 2.5 72B",   72_700_000_000,  null,         80,  8, 128),
        new("Qwen 3 32B",     32_800_000_000,  null,         64,  8, 128),
        new("Phi-3 mini",      3_820_000_000,  null,         32, 32,  96),
    ];

    private static double HeuristicKvPerToken(long kvParams) =>
        1.5 * Math.Sqrt(kvParams);

    // ---- KV heuristic vs exact architecture math ----------------------------

    /// <summary>
    /// The whole-corpus envelope. Worst observed: 0.209× (Llama 2 13B,
    /// legacy MHA) and 2.825× (Gemma 3 4B, single KV head). Bounds carry
    /// ~15% margin over the observations so minor future additions don't
    /// churn the suite — a REAL regression must blow through them.
    /// </summary>
    [Fact]
    public void Kv_Heuristic_Stays_Within_The_Corpus_Envelope()
    {
        foreach (var arch in Corpus)
        {
            var ratio = HeuristicKvPerToken(arch.KvParams) / arch.ActualKvBytesPerToken;
            Assert.True(ratio is >= 0.18 and <= 3.2,
                $"{arch.Name}: heuristic is {ratio:F3}× the exact KV — outside [0.18, 3.2]");
        }
    }

    /// <summary>
    /// The envelope restricted to GQA (KV heads ≤ 16) — the class of every
    /// model in the app's catalog. Much tighter on the low side: worst
    /// observed 0.479× (Gemma 2 9B) vs 0.209× for legacy MHA.
    /// </summary>
    [Fact]
    public void Kv_Heuristic_Is_Tight_For_Gqa_Architectures()
    {
        var gqa = Corpus.Where(a => !a.IsMha).ToList();
        Assert.True(gqa.Count >= 20, "corpus should stay mostly-GQA");

        foreach (var arch in gqa)
        {
            var ratio = HeuristicKvPerToken(arch.KvParams) / arch.ActualKvBytesPerToken;
            Assert.True(ratio is >= 0.45 and <= 3.0,
                $"{arch.Name}: heuristic is {ratio:F3}× the exact KV — outside [0.45, 3.0]");
        }
    }

    /// <summary>
    /// The confinement property: every serious underestimate (below the GQA
    /// floor) belongs to a legacy MHA architecture — KV heads == query
    /// heads, so KV grows faster than √params. This is the heuristic's one
    /// known blind spot; this test makes sure it never quietly grows a new
    /// one (e.g. a future GQA family the formula misses).
    /// </summary>
    [Fact]
    public void Kv_Underestimates_Beyond_The_Gqa_Floor_Are_Only_Mha()
    {
        foreach (var arch in Corpus)
        {
            var ratio = HeuristicKvPerToken(arch.KvParams) / arch.ActualKvBytesPerToken;
            if (ratio < 0.45)
                Assert.True(arch.IsMha,
                    $"{arch.Name} underestimates KV {ratio:F3}× but is NOT MHA — " +
                    "the heuristic has a new blind spot");
        }
    }

    /// <summary>
    /// The MoE refinement earns its place: with the ACTIVE count, every MoE
    /// architecture lands comfortably inside the GQA envelope.
    /// </summary>
    [Fact]
    public void Kv_With_Active_Count_Fits_Moe_Architectures()
    {
        var moe = Corpus.Where(a => a.ActiveParams is not null).ToList();
        Assert.Equal(5, moe.Count);

        foreach (var arch in moe)
        {
            var ratio = HeuristicKvPerToken(arch.KvParams) / arch.ActualKvBytesPerToken;
            Assert.True(ratio is >= 0.45 and <= 2.0,
                $"{arch.Name}: active-count heuristic is {ratio:F3}× the exact KV");
        }
    }

    /// <summary>
    /// Regression pin for the refinement's reason to exist: sizing KV from
    /// the TOTAL count inflates MoE models 1.8–2.8× (the attention layers
    /// are dense — experts only duplicate the FFN). If this ever stops being
    /// true, the active-count parsing is dead weight and can go.
    /// </summary>
    [Fact]
    public void Kv_With_Total_Count_Would_Inflate_Moe_Beyond_The_Envelope()
    {
        foreach (var arch in Corpus.Where(a => a.ActiveParams is not null))
        {
            var inflated = HeuristicKvPerToken(arch.TotalParams) / arch.ActualKvBytesPerToken;
            Assert.True(inflated > 1.7,
                $"{arch.Name}: total-count heuristic is only {inflated:F3}× — " +
                "the MoE refinement may no longer be needed");
        }
    }

    // ---- MoE label parsing --------------------------------------------------

    [Theory]
    [InlineData("26B-A4B", 4_000_000_000L)]     // Gemma 4 MoE (catalog label)
    [InlineData("35B-A3B", 3_000_000_000L)]     // Qwen 3.6 (catalog label)
    [InlineData("118B-A8B", 8_000_000_000L)]    // Laguna (catalog label)
    [InlineData("46.7B-A12.9B", 12_900_000_000L)] // Mixtral-style precision
    [InlineData("20B", 20_000_000_000L)]        // no -A part → total
    [InlineData("E4B", 4_000_000_000L)]         // effective label IS the active count
    [InlineData("Flash", 0L)]
    [InlineData("", 0L)]
    public void ParseActiveParameterCount_Labels(string label, long expected)
    {
        Assert.Equal(expected, ModelMemoryEstimator.ParseActiveParameterCount(label));
    }

    [Fact]
    public void Estimate_Kv_Uses_Active_Count_For_Moe_Labels()
    {
        var estimate = ModelMemoryEstimator.Estimate("46.7B-A12.9B", "Q4_K_M", 0);

        Assert.Equal(
            ModelMemoryEstimator.EstimateKvCacheBytes(12_900_000_000,
                ModelMemoryEstimator.DefaultContextSize),
            estimate.KvCacheBytes);
        Assert.NotEqual(
            ModelMemoryEstimator.EstimateKvCacheBytes(46_700_000_000,
                ModelMemoryEstimator.DefaultContextSize),
            estimate.KvCacheBytes);
    }

    // ---- Weight fallback vs real catalog file sizes --------------------------

    /// <summary>
    /// A snapshot of the real llama.app catalog: (params label, quant,
    /// GGUF file bytes). The ground truth the weight fallback is judged by.
    /// </summary>
    private static readonly (string Params, string Quant, ulong FileBytes)[] Catalog =
    [
        ("20B",     "mxfp4",  12_109_566_560),
        ("120B",    "mxfp4",  63_387_346_464),
        ("270M",    "Q4_0",      241_410_624),
        ("1B",      "Q4_0",      720_425_600),
        ("4B",      "Q4_0",    2_526_080_992),
        ("12B",     "Q4_0",    7_131_017_792),
        ("27B",     "Q4_0",   15_908_791_488),
        ("E4B",     "Q4_0",    4_590_807_392),
        ("E4B",     "Q8_0",    8_591_114_688),
        ("0.8B",    "Q4_0",      563_036_064),
        ("3B",      "Q4_K_M",  2_147_023_008),
        ("8B",      "Q4_K_M",  5_198_911_904),
        ("14B",     "Q8_0",   14_827_657_360),
        ("14B",     "Q4_K_M",  8_239_591_424),
        ("27B",     "Q4_K_M", 18_973_870_432),
        ("26B-A4B", "Q4_0",   14_618_145_824),
        ("35B-A3B", "Q4_K_M", 20_419_565_568),
        ("24B",     "Q4_K_M", 14_334_446_752),
        ("123B",    "Q4_K_M", 74_897_662_400),
        ("118B-A8B","Q4_K_M", 67_661_639_264),
    ];

    private static bool IsEffectiveLabel(string paramsLabel) =>
        paramsLabel.StartsWith('E');

    /// <summary>
    /// The weight fallback (params × bits/8, no file size) against the real
    /// catalog files. Honest labels land in [0.6, 1.1]× — the low end is
    /// small models (270M at 0.63×), whose files carry proportionally more
    /// non-quantized state (embeddings, metadata); the high end is MoE
    /// labels, where total × bits matches the file almost exactly because
    /// the experts ARE the quantized bulk. gemma-3n "E" labels are excluded
    /// — their weakness has its own pin below.
    /// </summary>
    [Fact]
    public void Weight_Fallback_Within_Envelope_For_Honest_Labels()
    {
        foreach (var (paramsLabel, quant, fileBytes) in Catalog.Where(c => !IsEffectiveLabel(c.Params)))
        {
            var bits = ModelMemoryEstimator.BitsPerWeight(quant);
            Assert.NotNull(bits);

            var fallback = (ulong)(ModelMemoryEstimator.ParseParameterCount(paramsLabel) * bits!.Value / 8.0);
            var ratio = (double)fallback / fileBytes;
            Assert.True(ratio is >= 0.6 and <= 1.1,
                $"{paramsLabel} {quant}: fallback is {ratio:F3}× the real file size — outside [0.6, 1.1]");
        }
    }

    /// <summary>
    /// Pinned weakness: an "E" (effective-params) label's fallback can't be
    /// trusted — E4B is ~4.6–8.6 GB on disk but its label implies 2.25–4.25 GB
    /// (the effective count hides ~2× of physical weights). This is why
    /// <c>Estimate</c> prefers the file size and why catalog entries always
    /// ship <c>sizeBytes</c>. If this test starts PASSING differently, the
    /// catalog's E-label semantics changed.
    /// </summary>
    [Fact]
    public void Weight_Fallback_Is_Documentably_Weak_For_Effective_Labels()
    {
        foreach (var (paramsLabel, quant, fileBytes) in Catalog.Where(c => IsEffectiveLabel(c.Params)))
        {
            var fallback = (ulong)(ModelMemoryEstimator.ParseParameterCount(paramsLabel)
                * ModelMemoryEstimator.BitsPerWeight(quant)!.Value / 8.0);
            var ratio = (double)fallback / fileBytes;
            Assert.True(ratio < 0.7,
                $"{paramsLabel} {quant}: fallback is {ratio:F3}× the file — E-labels were " +
                "supposed to be unreliable without a file size");
        }
    }

    /// <summary>
    /// The direction that matters: with the fallback weights plus overhead
    /// plus heuristic KV, the TOTAL estimate never falls below 90% of the
    /// conservative requirement (real file + 10% overhead, no KV) for any
    /// honest-label catalog entry — a fit check built on the estimate can't
    /// be fooled into loading a model that definitely won't fit. Checked
    /// against the real catalog, not the formula.
    /// </summary>
    [Fact]
    public void Total_Estimate_Never_Dangerously_Underestimates()
    {
        foreach (var (paramsLabel, quant, fileBytes) in Catalog.Where(c => !IsEffectiveLabel(c.Params)))
        {
            var estimate = ModelMemoryEstimator.Estimate(paramsLabel, quant, 0);
            var conservativeRequirement = fileBytes + fileBytes / 10; // real weights + overhead, KV as bonus

            Assert.True(estimate.TotalBytes >= conservativeRequirement * 0.9,
                $"{paramsLabel} {quant}: estimate {estimate.TotalBytes} < 90% of " +
                $"{conservativeRequirement} — dangerously under");
        }
    }

    /// <summary>
    /// The other direction, kept honest so the check doesn't become a
    /// blanket "nothing fits": the total estimate never exceeds 2× the
    /// conservative requirement. Worst observed is the 270M entry (1.92×),
    /// pushed up by the 256 MiB overhead floor dominating a tiny model —
    /// overestimating a 241 MB model is harmless for fit decisions.
    /// </summary>
    [Fact]
    public void Total_Estimate_Not_Unfairly_Over()
    {
        foreach (var (paramsLabel, quant, fileBytes) in Catalog.Where(c => !IsEffectiveLabel(c.Params)))
        {
            var estimate = ModelMemoryEstimator.Estimate(paramsLabel, quant, 0);
            var conservativeRequirement = fileBytes + fileBytes / 10;

            Assert.True(estimate.TotalBytes <= conservativeRequirement * 2.0,
                $"{paramsLabel} {quant}: estimate {estimate.TotalBytes} > 200% of " +
                $"{conservativeRequirement} — unfairly over");
        }
    }

    /// <summary>
    /// With a file size (the normal case — every catalog and local-cache
    /// entry has one), the estimate is exact on weights, so it must hug the
    /// realistic requirement: within [0.85, 1.35]× of (file + exact KV +
    /// 10% overhead). The lower end bites only where the KV heuristic
    /// itself underestimates (Gemma 2 9B class), and only by the KV share.
    /// </summary>
    [Fact]
    public void Total_Estimate_With_File_Size_Hugs_The_Realistic_Requirement()
    {
        // Catalog entries whose architecture is in the corpus.
        var cases = new (Arch Arch, ulong FileBytes)[]
        {
            (Corpus.First(a => a.Name == "Gemma 3 4B"),   2_526_080_992),
            (Corpus.First(a => a.Name == "Gemma 3 27B"), 15_908_791_488),
            (Corpus.First(a => a.Name == "GPT-OSS 20B"), 12_109_566_560),
        };

        foreach (var (arch, fileBytes) in cases)
        {
            // The catalog's GPT-OSS row says "20B" (no -A part) — the exact
            // labels the estimator will see.
            var label = arch.Name == "GPT-OSS 20B"
                ? "20B"
                : arch.Name == "Gemma 3 4B" ? "4B" : "27B";

            var estimate = ModelMemoryEstimator.Estimate(label, "Q4_0", fileBytes);
            var exactKv = (ulong)(arch.ActualKvBytesPerToken * ModelMemoryEstimator.DefaultContextSize);
            var realistic = fileBytes + exactKv + fileBytes / 10;

            var ratio = (double)estimate.TotalBytes / realistic;
            Assert.True(ratio is >= 0.85 and <= 1.35,
                $"{arch.Name}: estimate is {ratio:F3}× the realistic requirement");
        }
    }

    // ---- Dilution: KV error can't swing the total ----------------------------

    /// <summary>
    /// The property that makes the KV heuristic's wide envelope livable:
    /// weights dominate, so even a 2.8× KV error moves the TOTAL estimate by
    /// less than 15% for file-backed models. Verified on the corpus entries
    /// with real file sizes — if a future KV-frugal/cheap architecture and a
    /// new quant break this, the fit margins need re-deriving.
    /// </summary>
    [Fact]
    public void Kv_Error_Is_Diluted_In_The_Total_Estimate()
    {
        var cases = new (Arch Arch, string Label, ulong FileBytes)[]
        {
            (Corpus.First(a => a.Name == "Gemma 3 4B"),   "4B",  2_526_080_992),
            (Corpus.First(a => a.Name == "Gemma 3 27B"),  "27B", 15_908_791_488),
            (Corpus.First(a => a.Name == "GPT-OSS 20B"),  "20B", 12_109_566_560),
        };

        foreach (var (arch, label, fileBytes) in cases)
        {
            var heuristic = ModelMemoryEstimator.Estimate(label, "Q4_0", fileBytes);
            var exactKv = (ulong)(arch.ActualKvBytesPerToken * ModelMemoryEstimator.DefaultContextSize);
            var exactTotal = fileBytes + exactKv + fileBytes / 10;

            var deviation = Math.Abs((double)heuristic.TotalBytes / exactTotal - 1.0);
            Assert.True(deviation <= 0.15,
                $"{arch.Name}: total estimate deviates {deviation:P1} from the exact " +
                "requirement — KV error is no longer diluted");
        }
    }

    /// <summary>
    /// Fit-verdict stability: whenever the machine's budget has 30% headroom
    /// over the WORST-case (max of heuristic/exact) requirement, the verdict
    /// is "fits" under BOTH KV truths; whenever it can't even cover 80% of
    /// the bare weights, both say "doesn't fit". The heuristic's error can
    /// therefore only decide razor-thin cases — where saying "no" (the
    /// overestimate direction) is the acceptable mistake.
    /// </summary>
    [Fact]
    public void Fit_Verdicts_Are_Stable_Outside_The_Error_Band()
    {
        var arch = Corpus.First(a => a.Name == "Gemma 3 27B");
        const ulong fileBytes = 15_908_791_488;

        var heuristic = ModelMemoryEstimator.Estimate("27B", "Q4_0", fileBytes);
        var exactKv = (ulong)(arch.ActualKvBytesPerToken * ModelMemoryEstimator.DefaultContextSize);
        var exact = heuristic with { KvCacheBytes = exactKv };
        var worstCase = Math.Max(heuristic.TotalBytes, exact.TotalBytes);

        var gpu = new[]
        {
            new LlamaDevice
            {
                Id = "CUDA0", Name = "Test GPU", Kind = DeviceKind.Cuda,
                TotalBytes = ulong.MaxValue / 2, FreeBytes = ulong.MaxValue / 2,
            },
        };

        // Roomy budget: both KV truths agree it fits.
        var roomy = gpu.Select(d => d with
            { FreeBytes = (ulong)(worstCase * 1.3), TotalBytes = (ulong)(worstCase * 2) }).ToList();
        Assert.True(MemoryFit.Check(heuristic, roomy, null).Fits);
        Assert.True(MemoryFit.Check(exact, roomy, null).Fits);

        // Starved budget (CPU fallback, can't cover the weights): both agree it doesn't.
        var starvedRam = (ulong)((fileBytes * 0.8) / MemoryFit.CpuRamBudgetFraction);
        Assert.False(MemoryFit.Check(heuristic, [], starvedRam).Fits);
        Assert.False(MemoryFit.Check(exact, [], starvedRam).Fits);
    }
}
