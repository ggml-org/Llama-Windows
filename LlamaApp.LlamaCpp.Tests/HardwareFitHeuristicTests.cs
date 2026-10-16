using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the three-level hardware fit heuristic
/// (<see cref="HardwareFitHeuristic.Classify"/>) — the green/yellow/red
/// verdict for a model variation (f32, q8_0, q4_0, …) from catalog metadata
/// alone: the combined VRAM + RAM budget, the comfort/optimism bands around
/// it, and the fail-open rules that land unknowns on "might".
/// </summary>
public class HardwareFitHeuristicTests
{
    private static LlamaDevice Device(string id, string name, ulong freeMiB) => new()
    {
        Id = id,
        Name = name,
        TotalBytes = freeMiB << 20,
        FreeBytes = freeMiB << 20,
        Kind = DeviceQuery.KindFromId(id),
    };

    private static MemoryEstimate Estimate(ulong totalBytes) => new()
    {
        WeightBytes = totalBytes,
        KvCacheBytes = 0,
        OverheadBytes = 0,
        ContextSize = 4096,
    };

    /// <summary>A mid-range gaming box: 8 GiB free VRAM, 16 GB free RAM.</summary>
    private static readonly LlamaDevice[] MidRangeGpu = { Device("CUDA0", "NVIDIA GeForce RTX 4060", 8 << 10) };

    [Fact]
    public void Variations_Of_One_Model_Tint_Green_Yellow_Red()
    {
        // The flagship use case: the same 20B model in three quants on one
        // machine (8 GiB VRAM + 16 GB RAM → ~20.6 GB combined budget) —
        // f32 is a no way, q8_0 a might, q4_0 a fit.
        var f32 = HardwareFitHeuristic.Classify(
            ModelMemoryEstimator.Estimate("20B", "F32", 0), MidRangeGpu, 16UL << 30);
        var q8 = HardwareFitHeuristic.Classify(
            ModelMemoryEstimator.Estimate("20B", "Q8_0", 0), MidRangeGpu, 16UL << 30);
        var q4 = HardwareFitHeuristic.Classify(
            ModelMemoryEstimator.Estimate("20B", "Q4_0", 0), MidRangeGpu, 16UL << 30);

        Assert.Equal(HardwareFitLevel.NoWay, f32.Level);
        Assert.Equal(HardwareFitLevel.Might, q8.Level);
        Assert.Equal(HardwareFitLevel.Fit, q4.Level);
    }

    [Fact]
    public void Fits_Fully_In_Gpu_Vram_Notes_The_Gpu_Run()
    {
        // The user's sample machine: 14143 MiB free on the RTX 4060 Ti.
        var devices = new[] { Device("CUDA0", "NVIDIA GeForce RTX 4060 Ti", 14_143) };
        var result = HardwareFitHeuristic.Classify(Estimate(13UL << 30), devices, 16UL << 30);

        Assert.Equal(HardwareFitLevel.Fit, result.Level);
        Assert.Contains("GPU memory", result.Note);
    }

    [Fact]
    public void Over_Vram_But_In_The_Combined_Budget_Fits_Split()
    {
        // 12 GB on an 8 GiB GPU + 32 GB RAM machine: llama.cpp splits the
        // layers across GPU and host — fits, with the split spelled out.
        var result = HardwareFitHeuristic.Classify(Estimate(12_000_000_000UL), MidRangeGpu, 32UL << 30);

        Assert.Equal(HardwareFitLevel.Fit, result.Level);
        Assert.Contains("split", result.Note);
    }

    [Fact]
    public void Cpu_Only_Machine_Fits_In_System_Memory()
    {
        var result = HardwareFitHeuristic.Classify(Estimate(10_000_000_000UL), [], 32UL << 30);

        Assert.Equal(HardwareFitLevel.Fit, result.Level);
        Assert.Contains("system memory", result.Note);
    }

    [Fact]
    public void Tight_Within_The_Budget_Is_Might()
    {
        // 11.5 GB against a 12 GB budget (16 GB free RAM, no GPU): inside
        // the budget but past the comfort band — little headroom.
        var result = HardwareFitHeuristic.Classify(Estimate(11_500_000_000UL), [], 16_000_000_000UL);

        Assert.Equal(HardwareFitLevel.Might, result.Level);
        Assert.Contains("Tight fit", result.Note);
    }

    [Fact]
    public void Slightly_Over_The_Budget_Is_Might()
    {
        // 12.5 GB against a 12 GB budget — within the optimism band (the
        // estimator's error bars + closing background apps).
        var result = HardwareFitHeuristic.Classify(Estimate(12_500_000_000UL), [], 16_000_000_000UL);

        Assert.Equal(HardwareFitLevel.Might, result.Level);
        Assert.Contains("Might fit", result.Note);
    }

    [Fact]
    public void Far_Over_The_Budget_Is_NoWay()
    {
        var result = HardwareFitHeuristic.Classify(Estimate(20_000_000_000UL), [], 16_000_000_000UL);

        Assert.Equal(HardwareFitLevel.NoWay, result.Level);
        Assert.Contains("Won't fit", result.Note);
    }

    [Fact]
    public void Unknown_Estimate_Is_Might_Never_Blocks()
    {
        var result = HardwareFitHeuristic.Classify(Estimate(0), MidRangeGpu, 16UL << 30);

        Assert.Equal(HardwareFitLevel.Might, result.Level);
    }

    [Fact]
    public void Failed_Hardware_Probe_Is_Might_Never_Blocks()
    {
        var result = HardwareFitHeuristic.Classify(Estimate(4_000_000_000UL), [], null);

        Assert.Equal(HardwareFitLevel.Might, result.Level);
    }

    [Fact]
    public void Devices_With_Unknown_Ram_Conservatively_Budget_Vram_Alone()
    {
        // No RAM number: the budget is just the 4 GiB of free VRAM — 5 GB
        // is over it but within the optimism band (partial offload could
        // still stumble through).
        var devices = new[] { Device("CUDA0", "Some GPU", 4 << 10) };
        var result = HardwareFitHeuristic.Classify(Estimate(5_000_000_000UL), devices, null);

        Assert.Equal(HardwareFitLevel.Might, result.Level);
    }

    [Fact]
    public void Verdict_Carries_The_Numbers_Behind_The_Level()
    {
        var result = HardwareFitHeuristic.Classify(Estimate(10_000_000_000UL), MidRangeGpu, 32UL << 30);

        Assert.Equal(10_000_000_000UL, result.RequiredBytes);
        Assert.Equal((8UL << 30) + (ulong)((32UL << 30) * 0.75), result.BudgetBytes);
        Assert.NotEmpty(result.Note);
        Assert.Single(result.Devices);
    }

    // ---- Byte-based bands (shared with the context-length ladder) ----

    [Theory]
    [InlineData(90, HardwareFitLevel.Fit)]     // at the comfort line
    [InlineData(91, HardwareFitLevel.Might)]   // tight inside the budget
    [InlineData(125, HardwareFitLevel.Might)]  // at the optimism line
    [InlineData(126, HardwareFitLevel.NoWay)]
    public void Byte_Bands_Share_One_Threshold_Set(int percentOfBudget, HardwareFitLevel level)
    {
        Assert.Equal(level,
            HardwareFitHeuristic.Classify((ulong)percentOfBudget * 1_000_000UL, 100_000_000UL));
    }

    [Fact]
    public void Unknown_Requirement_Or_Budget_Is_Might_Never_NoWay()
    {
        Assert.Equal(HardwareFitLevel.Might, HardwareFitHeuristic.Classify(0, 100));
        Assert.Equal(HardwareFitLevel.Might, HardwareFitHeuristic.Classify(100, 0));
    }
}
