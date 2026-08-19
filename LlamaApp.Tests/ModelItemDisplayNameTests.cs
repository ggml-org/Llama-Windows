using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// <see cref="ModelItem.RowDisplayName"/> — the row/header name without the
/// tag-carried parts (trailing "(quant)" suffix, standalone params token).
/// </summary>
public sealed class ModelItemDisplayNameTests
{
    [Theory]
    // The catalog shape: "Pretty Name (QUANT)" — both parts stripped.
    [InlineData("Gemma 3 1B (Q4_0)", "Q4_0", "1B", "Gemma 3")]
    [InlineData("GPT-OSS 20B (mxfp4)", "mxfp4", "20B", "GPT-OSS")]
    [InlineData("Gemma 4 E2B (Q4_K_M)", "Q4_K_M", "E2B", "Gemma 4")]
    // No quant suffix — the params token still goes.
    [InlineData("Gemma 3 1B", null, "1B", "Gemma 3")]
    // Params unknown — only the quant suffix is stripped.
    [InlineData("Gemma 3 1B (Q4_0)", "Q4_0", "", "Gemma 3 1B")]
    // A multi-word params phrase is stripped as a phrase.
    [InlineData("Ministral 3 3B Reasoning (Q4_K_M)", "Q4_K_M", "3B Reasoning", "Ministral 3")]
    // A params-looking substring inside a hyphenated slug is part of the
    // name, not a tag — it stays.
    [InlineData("Ministral-3-3B-Reasoning (Q4_K_M)", "Q4_K_M", "3B", "Ministral-3-3B-Reasoning")]
    [InlineData("Qwen3-4B", "Q4_K_M", "4B", "Qwen3-4B")]
    // Substring of a longer token ("11B") is not a match either.
    [InlineData("Gemma 3 11B (Q4_0)", "Q4_0", "1B", "Gemma 3 11B")]
    // The name IS the params token — stripping it would leave nothing.
    [InlineData("20B", null, "20B", "20B")]
    public void RowDisplayName_Strips_The_Tag_Carried_Parts(
        string name, string? quant, string parameters, string expected)
    {
        var item = new ModelItem { Name = name, Quant = quant, Parameters = parameters };

        Assert.Equal(expected, item.RowDisplayName);
    }

    [Fact]
    public void HasQuant_Tracks_The_Quant_Label()
    {
        Assert.True(new ModelItem { Quant = "Q4_0" }.HasQuant);
        Assert.False(new ModelItem { Quant = null }.HasQuant);
        Assert.False(new ModelItem { Quant = "" }.HasQuant);
    }
}
