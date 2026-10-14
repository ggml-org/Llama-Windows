using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the family-name brand detection (Common) that keeps local models' logo
/// tiles from rendering empty (an imported .gguf, a folder-enrolled model, a
/// Hub repo outside the catalog — none of them have a catalog brand). Known
/// family keywords match inside the model's name; the mapping must mirror the
/// bundled logos' families, stay case-insensitive, and NOT misfire on the
/// lookalikes (GPTQ the quantization format is not GPT the family).
/// </summary>
public sealed class LocalBrandFromNameTests
{
    // ---- known families ----

    [Theory]
    [InlineData("gemma-3-1b-it-qat-Q4_0", "gemma")]        // enrolled/import stem
    [InlineData("Gemma-4-E4B-it-Q4_0", "gemma")]            // case-insensitive
    [InlineData("Qwen3.8-27B-BF16", "qwen")]               // digit-suffixed family
    [InlineData("qwen3-coder-30b", "qwen")]
    [InlineData("gpt-oss-20b-mxfp4", "gpt")]
    [InlineData("Mistral-7B-Instruct-v0.2", "mistral")]
    [InlineData("Ministral-8B-Instruct", "mistral")]
    [InlineData("devstral-small", "mistral")]
    [InlineData("GLM-4.7-Air-Q4_0", "z")]
    [InlineData("Nemotron-Super-49B", "nvidia")]
    [InlineData("ggml-org/gemma-3-1b-it-qat-GGUF:Q4_0", "gemma")] // full server id
    [InlineData("mimo-v2.6-9B", null)]
    [InlineData("Llama-3.2-1B-Instruct", null)]
    [InlineData("llama-8b", null)]
    [InlineData("MyPrivateFineTune-Q8_0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FamilyKeywords_MatchInsideTheName(string? name, string? expected)
    {
        Assert.Equal(expected, ModelBrandNames.BrandFromName(name));
    }

    // ---- the GPTQ lookalike ----

    [Theory]
    [InlineData("MyModel-GPTQ-4bit")]      // quantization format, not the family
    [InlineData("model_gptq")]
    [InlineData("GPTQ.gguf")]
    public void Gptq_TheQuantFormat_IsNotTheGptFamily(string name)
    {
        Assert.Null(ModelBrandNames.BrandFromName(name));
    }

    // ---- boundaries ----

    [Theory]
    [InlineData("gpt-oss-20b", "gpt")]     // hyphen boundary
    [InlineData("openai_gpt", "gpt")]       // underscore boundary
    [InlineData("deepseek-gpt4-alternative", "gpt")] // mid-name after a boundary
    public void WordBoundaries_AreRespected(string name, string? expected)
    {
        Assert.Equal(expected, ModelBrandNames.BrandFromName(name));
    }
}
