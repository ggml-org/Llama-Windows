using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="QuantizationPresentation"/> — the friendly
/// quality names behind the family details variant picker. A friendly name
/// must exist for every known quant tier, and an unknown quant must fall
/// back to its own label (never render blank).
/// </summary>
public class QuantizationPresentationTests
{
    [Theory]
    [InlineData(@"C:\Models\Gemma-3-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData(@"C:\Models\Qwen.q8_0.gguf", "Q8_0")]
    [InlineData(@"C:\Models\Model-IQ2_XXS.gguf", "IQ2_XXS")]
    [InlineData(@"C:\Models\Model-IQ4_NL.gguf", "IQ4_NL")]
    [InlineData(@"C:\Models\Model-Q6_K-00001-of-00003.gguf", "Q6_K")]
    [InlineData(@"C:\Models\Model-Q4_0_4_8.gguf", "Q4_0_4_8")]
    [InlineData(@"C:\Models\gpt-oss-mxfp4.gguf", "MXFP4")]
    [InlineData(@"C:\Models\Model (BF16).gguf", "BF16")]
    [InlineData(@"C:\Models\Model-F16.gguf", "F16")]
    [InlineData(@"C:\Q4_K_M\Model.gguf", null)]
    [InlineData(@"C:\Models\Model-NotQ4_K_M.gguf", null)]
    [InlineData(@"C:\Models\Model.gguf", null)]
    [InlineData(null, null)]
    public void FileNamesExposeOnlyStandaloneQuantLabels(string? path, string? expected)
        => Assert.Equal(expected, QuantizationPresentation.FromFileName(path));

    [Theory]
    [InlineData("Q2_K", "Compact")]
    [InlineData("Q3_K_M", "Compact")]
    [InlineData("IQ2_XXS", "Compact")]
    [InlineData("IQ4_XS", "Compact")]
    [InlineData("Q4_0", "Balanced")]
    [InlineData("Q4_K_M", "Balanced")]
    [InlineData("mxfp4", "Balanced")]
    [InlineData("MXFP4", "Balanced")]
    [InlineData("Q5_K_M", "Higher quality")]
    [InlineData("Q6_K", "Higher quality")]
    [InlineData("Q8_0", "Higher quality")]
    [InlineData("F16", "Higher quality")]
    [InlineData("BF16", "Higher quality")]
    [InlineData("F32", "Higher quality")]
    public void Known_Quants_Map_To_Friendly_Names(string quant, string expected)
    {
        Assert.Equal(expected, QuantizationPresentation.FriendlyName(quant));
    }

    [Theory]
    [InlineData("Q9_K")]    // unknown Q tier keeps its label
    [InlineData("GGML")]    // not a quant at all
    public void Unknown_Quants_Fall_Back_To_The_Raw_Label(string quant)
    {
        Assert.Equal(quant, QuantizationPresentation.FriendlyName(quant));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_Quants_Never_Throw(string? quant)
    {
        // Blank input round-trips rather than crashing the variant row.
        Assert.Equal(quant ?? "", QuantizationPresentation.FriendlyName(quant));
    }

    [Fact]
    public void Mapping_Is_Case_Insensitive_For_Prefixes()
    {
        Assert.Equal("Balanced", QuantizationPresentation.FriendlyName("q4_k_m"));
        Assert.Equal("Compact", QuantizationPresentation.FriendlyName("iq3_k"));
    }
}
