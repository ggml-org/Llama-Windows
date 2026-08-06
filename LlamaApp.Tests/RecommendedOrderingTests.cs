using LlamaApp.HuggingFace;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="RecommendedOrdering"/> — the catalog's Featured
/// flag must order the Recommended list (featured first) without disturbing
/// the catalog order within each group.
/// </summary>
public class RecommendedOrderingTests
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
    public void Featured_Families_Sort_First()
    {
        var ordered = RecommendedOrdering.OrderForDisplay(new[]
        {
            Repo("a/plain"),
            Repo("b/featured", featured: true),
            Repo("c/plain"),
        });

        Assert.Equal(new[] { "b/featured", "a/plain", "c/plain" },
            ordered.Select(r => r.Name));
    }

    [Fact]
    public void Ordering_Is_Stable_Within_Each_Group()
    {
        // OrderByDescending is stable: catalog order is preserved within the
        // featured group and within the plain group.
        var ordered = RecommendedOrdering.OrderForDisplay(new[]
        {
            Repo("a/plain1"),
            Repo("b/featured1", featured: true),
            Repo("c/plain2"),
            Repo("d/featured2", featured: true),
            Repo("e/plain3"),
        });

        Assert.Equal(new[]
        {
            "b/featured1", "d/featured2",
            "a/plain1", "c/plain2", "e/plain3",
        }, ordered.Select(r => r.Name));
    }

    [Fact]
    public void Empty_Input_Yields_Empty_Output()
    {
        Assert.Empty(RecommendedOrdering.OrderForDisplay(new List<Repository>()));
    }
}
