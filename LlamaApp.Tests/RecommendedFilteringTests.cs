using LlamaApp.HuggingFace;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="RecommendedFiltering"/> — only catalog families
/// marked <c>featured</c> may render in the Recommended list, with catalog
/// order preserved among them.
/// </summary>
public class RecommendedFilteringTests
{
    private static Repository Repo(string name, bool featured = false) => new()
    {
        Name = name,
        Description = "",
        License = "",
        Parameters = "",
        Size = "",
        Vision = false,
        Featured = featured,
    };

    [Fact]
    public void Only_Featured_Families_Are_Kept()
    {
        var filtered = RecommendedFiltering.FilterForDisplay(new[]
        {
            Repo("a/plain"),
            Repo("b/featured", featured: true),
            Repo("c/plain"),
        });

        Assert.Equal(new[] { "b/featured" },
            filtered.Select(r => r.Name));
    }

    [Fact]
    public void Catalog_Order_Is_Preserved()
    {
        var filtered = RecommendedFiltering.FilterForDisplay(new[]
        {
            Repo("a/plain1"),
            Repo("b/featured1", featured: true),
            Repo("c/plain2"),
            Repo("d/featured2", featured: true),
            Repo("e/plain3"),
        });

        Assert.Equal(new[] { "b/featured1", "d/featured2" },
            filtered.Select(r => r.Name));
    }

    [Fact]
    public void No_Featured_Families_Yields_Empty_Output()
    {
        var filtered = RecommendedFiltering.FilterForDisplay(new[]
        {
            Repo("a/plain"),
            Repo("b/plain"),
        });

        Assert.Empty(filtered);
    }

    [Fact]
    public void Empty_Input_Yields_Empty_Output()
    {
        Assert.Empty(RecommendedFiltering.FilterForDisplay(new List<Repository>()));
    }
}
