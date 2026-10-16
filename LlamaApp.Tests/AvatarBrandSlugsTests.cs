using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="AvatarCache.BrandSlugs"/> — the Hub actor slug
/// candidates a brand label ("Liquid AI") is looked up with. The Hub
/// resolves slugs case-insensitively, so candidates are de-duplicated; the
/// space-stripped form leads (the shape nearly every org/user slug takes —
/// "LiquidAI", "jaredpalmer"), then the hyphenated-lowercase and the as-is
/// forms. Pure — no network, no XAML.
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
}
