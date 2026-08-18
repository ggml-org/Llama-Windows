using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the model-memory estimation math — parameter-count parsing
/// (the catalog's label shapes), quant→bits mapping, and the weight/KV/
/// overhead composition.
/// </summary>
public class ModelMemoryEstimatorTests
{
    // ---- Parameter-count parsing --------------------------------------------

    [Theory]
    [InlineData("20B", 20_000_000_000L)]
    [InlineData("120B", 120_000_000_000L)]
    [InlineData("270M", 270_000_000L)]
    [InlineData("1B", 1_000_000_000L)]
    [InlineData("0.8B", 800_000_000L)]
    public void ParseParameterCount_Standard_Labels(string label, long expected)
    {
        Assert.Equal(expected, ModelMemoryEstimator.ParseParameterCount(label));
    }

    [Theory]
    [InlineData("E2B", 2_000_000_000L)]   // gemma-3n effective-size label
    [InlineData("E4B", 4_000_000_000L)]
    public void ParseParameterCount_Effective_Size_Labels(string label, long expected)
    {
        Assert.Equal(expected, ModelMemoryEstimator.ParseParameterCount(label));
    }

    [Theory]
    [InlineData("26B-A4B", 26_000_000_000L)]  // MoE: TOTAL params — memory holds all experts
    [InlineData("35B-A3B", 35_000_000_000L)]
    [InlineData("118B-A8B", 118_000_000_000L)]
    public void ParseParameterCount_MoE_Labels_Use_Total_Count(string label, long expected)
    {
        Assert.Equal(expected, ModelMemoryEstimator.ParseParameterCount(label));
    }

    [Theory]
    [InlineData("3B Reasoning", 3_000_000_000L)] // decorated label — leading number
    public void ParseParameterCount_Decorated_Labels(string label, long expected)
    {
        Assert.Equal(expected, ModelMemoryEstimator.ParseParameterCount(label));
    }

    [Theory]
    [InlineData("Flash")]      // GLM 4.7 "Flash" — no number at all
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ParseParameterCount_Unparseable_Returns_Zero(string? label)
    {
        Assert.Equal(0, ModelMemoryEstimator.ParseParameterCount(label));
    }

    // ---- Quant → bits-per-weight ---------------------------------------------

    [Theory]
    [InlineData("F32", 32.0)]
    [InlineData("F16", 16.0)]
    [InlineData("Q8_0", 8.5)]
    [InlineData("Q6_K", 6.5625)]
    [InlineData("Q5_K_M", 5.68)]
    [InlineData("Q4_K_M", 4.85)]
    [InlineData("Q4_0", 4.5)]
    [InlineData("Q3_K_M", 3.91)]
    [InlineData("Q2_K", 2.56)]
    [InlineData("mxfp4", 4.25)]
    [InlineData("IQ4_XS", 4.25)]
    public void BitsPerWeight_Known_Quants(string quant, double expected)
    {
        Assert.Equal(expected, ModelMemoryEstimator.BitsPerWeight(quant));
    }

    [Theory]
    [InlineData("  q4_k_m  ")] // trimming + case-insensitivity
    public void BitsPerWeight_Normalizes_Input(string quant)
    {
        Assert.Equal(4.85, ModelMemoryEstimator.BitsPerWeight(quant));
    }

    [Theory]
    [InlineData("Q9_9_FANCY")]
    [InlineData("")]
    [InlineData(null)]
    public void BitsPerWeight_Unknown_Returns_Null(string? quant)
    {
        Assert.Null(ModelMemoryEstimator.BitsPerWeight(quant));
    }

    // ---- Estimate composition -------------------------------------------------

    [Fact]
    public void Estimate_Prefers_File_Size_Over_Param_Math()
    {
        // The GGUF file size is the best weight signal: a 12.1 GB mxfp4 file
        // wins over 20B × 4.25 bits ≈ 10.6 GB.
        const ulong fileSize = 12_109_566_560; // gpt-oss-20b mxfp4 catalog entry

        var estimate = ModelMemoryEstimator.Estimate("20B", "mxfp4", fileSize);

        Assert.Equal(fileSize, estimate.WeightBytes);
        Assert.False(estimate.IsUnknown);
        Assert.True(estimate.TotalBytes > fileSize); // KV + overhead added on top
    }

    [Fact]
    public void Estimate_Falls_Back_To_Param_Math_Without_File_Size()
    {
        var estimate = ModelMemoryEstimator.Estimate("4B", "Q4_0", 0);

        // 4e9 params × 4.5 bits / 8 = 2.25 GB of weights.
        Assert.Equal(2_250_000_000UL, estimate.WeightBytes);
    }

    [Fact]
    public void Estimate_Unknown_Inputs_Yield_Unknown_Estimate()
    {
        var estimate = ModelMemoryEstimator.Estimate("Flash", "Q9_NEW", 0);

        Assert.True(estimate.IsUnknown);
        Assert.Equal(0UL, estimate.TotalBytes);
    }

    [Fact]
    public void Estimate_Overhead_Is_10_Percent_Of_Weights_With_Floor()
    {
        var big = ModelMemoryEstimator.Estimate("20B", "mxfp4", 12_109_566_560);
        Assert.Equal(1_210_956_656UL, big.OverheadBytes);

        // A 100 MB model's 10% (10 MB) is below the 256 MiB floor.
        var small = ModelMemoryEstimator.Estimate("270M", "Q4_0", 100_000_000);
        Assert.Equal(256UL << 20, small.OverheadBytes);
    }

    // ---- KV cache heuristic ---------------------------------------------------

    [Theory]
    [InlineData("8B", 8192, 1_100_000_000UL)]   // Llama-3 8B f16 KV is ~1.07 GB
    [InlineData("70B", 8192, 3_300_000_000UL)]  // Llama-2 70B is ~2.7 GB
    public void EstimateKvCacheBytes_Calibration(string paramLabel, int ctx, ulong expectedMax)
    {
        var kv = ModelMemoryEstimator.EstimateKvCacheBytes(
            ModelMemoryEstimator.ParseParameterCount(paramLabel), ctx);
        Assert.InRange(kv, 1UL, expectedMax);
    }

    [Fact]
    public void EstimateKvCacheBytes_Scales_With_Context()
    {
        var at4k = ModelMemoryEstimator.EstimateKvCacheBytes(8_000_000_000, 4096);
        var at8k = ModelMemoryEstimator.EstimateKvCacheBytes(8_000_000_000, 8192);

        // Linear in the context size (±1 byte of integer truncation).
        Assert.InRange(at8k, at4k * 2 - 1, at4k * 2 + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EstimateKvCacheBytes_Unknown_Params_Is_Zero(long parameters)
    {
        Assert.Equal(0UL, ModelMemoryEstimator.EstimateKvCacheBytes(parameters));
    }
}
