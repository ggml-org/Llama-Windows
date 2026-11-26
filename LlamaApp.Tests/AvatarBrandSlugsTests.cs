using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="AvatarCache.BrandSlugs"/> and
/// <see cref="AvatarCache.ModelSlugs"/> — the Hub actor slug candidates a
/// model is looked up with: the original source repo's author first, then
/// the brand label's plausible slug forms ("Liquid AI" → LiquidAI /
/// liquid-ai / …). The Hub resolves slugs case-insensitively, so candidates
/// are de-duplicated; the space-stripped form leads (the shape nearly every
/// org/user slug takes — "LiquidAI", "jaredpalmer"), then the
/// hyphenated-lowercase and the as-is forms. Pure — no network, no XAML.
/// </summary>
public class AvatarBrandSlugsTests
{
    [Fact]
    public void Brand_Labels_Yield_The_Plausible_Slug_Forms_In_Order()
    {
        Assert.Equal(
            new[] { "LiquidAI", "liquid-ai", "Liquid AI" },
            AvatarCache.BrandSlugs("Liquid AI"));
    }

    [Fact]
    public void Single_Word_Brands_Deduplicate_To_One_Candidate()
    {
        // The Hub resolves slugs case-insensitively — "deepseek" and
        // "DeepSeek" are the same lookup, so only the first is kept.
        Assert.Equal(new[] { "DeepSeek" }, AvatarCache.BrandSlugs("DeepSeek"));
    }

    [Fact]
    public void Surrounding_Whitespace_Is_Trimmed()
    {
        Assert.Equal(new[] { "Meta" }, AvatarCache.BrandSlugs("  Meta  "));
    }

    [Fact]
    public void Empty_Brands_Yield_No_Candidates()
    {
        Assert.Empty(AvatarCache.BrandSlugs(""));
        Assert.Empty(AvatarCache.BrandSlugs("   "));
    }

    [Fact]
    public void Multi_Word_Brands_Get_A_Hyphenated_Lowercase_Candidate()
    {
        Assert.Equal(
            new[] { "ConvAIInnovations", "convai-innovations", "ConvAI Innovations" },
            AvatarCache.BrandSlugs("ConvAI Innovations"));
    }

    [Fact]
    public void Mirrored_Models_Look_Up_The_Source_Author()
    {
        // Aion is served from the ggml-org GGUF mirror but published by
        // Microsoft — "microsoft" (the source repo's author) subsumes the
        // "Microsoft" brand candidate (the Hub resolves slugs
        // case-insensitively), so a single lookup remains.
        Assert.Equal(
            new[] { "microsoft" },
            AvatarCache.ModelSlugs("microsoft/Aion-1.0-Instruct-2.3B", "Microsoft"));
    }

    [Fact]
    public void Distinct_Source_Authors_Precede_Brand_Candidates()
    {
        Assert.Equal(
            new[] { "deepseek-ai", "LiquidAI", "liquid-ai", "Liquid AI" },
            AvatarCache.ModelSlugs("deepseek-ai/DeepSeek-V3", "Liquid AI"));
    }

    [Fact]
    public void Blank_Sources_Fall_Back_To_The_Brand_Candidates()
    {
        Assert.Equal(AvatarCache.BrandSlugs("Liquid AI"), AvatarCache.ModelSlugs(null, "Liquid AI"));
        Assert.Equal(AvatarCache.BrandSlugs("Liquid AI"), AvatarCache.ModelSlugs("", "Liquid AI"));
        Assert.Equal(AvatarCache.BrandSlugs("Liquid AI"), AvatarCache.ModelSlugs("   ", "Liquid AI"));
    }

    [Fact]
    public void Duplicate_Candidates_Are_Removed_Case_Insensitively()
    {
        // The source author "liquidai" leads and the brand's "LiquidAI"
        // form — the same Hub actor — is dropped after it; later forms stay
        // in order.
        Assert.Equal(
            new[] { "liquidai", "liquid-ai", "Liquid AI" },
            AvatarCache.ModelSlugs("liquidai/LFM-2.5", "Liquid AI"));
    }
}
