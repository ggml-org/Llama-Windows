using System.Collections.ObjectModel;
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

    // ----- Fit-first partition ----------------------------------------------

    private static ModelItem Item(string name, bool fits) => new()
    {
        Name = name,
        FitsOnDevice = fits,
    };

    [Fact]
    public void Partition_Puts_Fitting_Models_First()
    {
        var a = Item("a", fits: false);
        var b = Item("b", fits: true);
        var c = Item("c", fits: false);
        var d = Item("d", fits: true);

        var ordered = RecommendedFiltering.PartitionFitFirst(
            new[] { a, b, c, d }, i => i.FitsOnDevice);

        // b, d (fitting) first, then a, c — catalog order kept within each half.
        Assert.Equal(new[] { b, d, a, c }, ordered);
    }

    [Fact]
    public void Partition_With_All_Fitting_Keeps_The_Original_Order()
    {
        var a = Item("a", fits: true);
        var b = Item("b", fits: true);

        var ordered = RecommendedFiltering.PartitionFitFirst(
            new[] { a, b }, i => i.FitsOnDevice);

        Assert.Equal(new[] { a, b }, ordered);
    }

    [Fact]
    public void Partition_With_None_Fitting_Keeps_The_Original_Order()
    {
        var a = Item("a", fits: false);
        var b = Item("b", fits: false);

        var ordered = RecommendedFiltering.PartitionFitFirst(
            new[] { a, b }, i => i.FitsOnDevice);

        Assert.Equal(new[] { a, b }, ordered);
    }

    // ----- Applying an order to the observable collection --------------------

    [Fact]
    public void ApplyOrder_Reorders_The_Collection_In_Place()
    {
        var a = Item("a", fits: false);
        var b = Item("b", fits: true);
        var c = Item("c", fits: true);
        var collection = new ObservableCollection<ModelItem> { a, b, c };

        RecommendedFiltering.ApplyOrder(collection, new[] { b, c, a });

        Assert.Equal(new[] { b, c, a }, collection);
    }

    [Fact]
    public void ApplyOrder_Raises_Move_Notifications_Not_Reset()
    {
        var a = Item("a", fits: false);
        var b = Item("b", fits: true);
        var collection = new ObservableCollection<ModelItem> { a, b };

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        RecommendedFiltering.ApplyOrder(collection, new[] { b, a });

        // Move keeps the rows alive in the ItemsRepeater; a Reset (clear +
        // refill) would tear them down and flash the list.
        Assert.All(actions, action => Assert.Equal(
            System.Collections.Specialized.NotifyCollectionChangedAction.Move, action));
    }

    [Fact]
    public void ApplyOrder_No_Op_When_Already_In_Order()
    {
        var a = Item("a", fits: true);
        var b = Item("b", fits: false);
        var collection = new ObservableCollection<ModelItem> { a, b };

        var raised = false;
        collection.CollectionChanged += (_, _) => raised = true;

        RecommendedFiltering.ApplyOrder(collection, new[] { a, b });

        Assert.False(raised);
    }

    [Fact]
    public void ApplyOrder_Rejects_Mismatched_Elements()
    {
        var a = Item("a", fits: true);
        var collection = new ObservableCollection<ModelItem> { a };

        Assert.Throws<ArgumentException>(() =>
            RecommendedFiltering.ApplyOrder(collection, new[] { Item("other", true) }));
        Assert.Throws<ArgumentException>(() =>
            RecommendedFiltering.ApplyOrder(collection, Array.Empty<ModelItem>()));
    }
}
