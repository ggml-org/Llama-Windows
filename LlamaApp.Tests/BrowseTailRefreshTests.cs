using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="BrowseTailRefresh"/> — the browse tail (separator
/// + family rows) must be rebuilt exactly when its desired content changes,
/// and left untouched (keeping the ListView's realized containers) on every
/// other list change. A false "no rebuild" would strand stale rows; a false
/// "rebuild" reintroduces the per-row tear-down churn the batching removed.
/// Rows are compared by reference and order, as the family view-models are.
/// </summary>
public class BrowseTailRefreshTests
{
    [Fact]
    public void Never_Built_Needs_A_Rebuild()
        => Assert.True(BrowseTailRefresh.NeedsRebuild<object>(
            null, [], builtWithSeparator: false, withSeparator: false));

    [Fact]
    public void Same_Instances_And_Separator_State_Needs_No_Rebuild()
    {
        var a = new object();
        var b = new object();
        Assert.False(BrowseTailRefresh.NeedsRebuild(
            new[] { a, b }, new[] { a, b },
            builtWithSeparator: true, withSeparator: true));
    }

    [Fact]
    public void Empty_Tail_Stays_Empty_Without_A_Rebuild()
        => Assert.False(BrowseTailRefresh.NeedsRebuild(
            Array.Empty<object>(), Array.Empty<object>(),
            builtWithSeparator: false, withSeparator: false));

    [Fact]
    public void Separator_State_Flip_Needs_A_Rebuild()
        // The one case per-row adds/removes hit: the separator exists only
        // when both an installed section AND families are present.
        => Assert.True(BrowseTailRefresh.NeedsRebuild(
            Array.Empty<object>(), Array.Empty<object>(),
            builtWithSeparator: true, withSeparator: false));

    [Fact]
    public void Added_Or_Removed_Family_Needs_A_Rebuild()
    {
        var a = new object();
        var b = new object();
        Assert.True(BrowseTailRefresh.NeedsRebuild(new[] { a }, new[] { a, b }, true, true));
        Assert.True(BrowseTailRefresh.NeedsRebuild(new[] { a, b }, new[] { a }, true, true));
    }

    [Fact]
    public void Replaced_Family_Instance_Needs_A_Rebuild()
        // Reference identity: equal-valued rows are still a rebuild (a catalog
        // retry re-wraps every family).
        => Assert.True(BrowseTailRefresh.NeedsRebuild(
            new object[] { new() }, new object[] { new() }, true, true));

    [Fact]
    public void Reordered_Families_Need_A_Rebuild()
        // The fit evaluation re-partitions the families after the probe lands.
    {
        var a = new object();
        var b = new object();
        Assert.True(BrowseTailRefresh.NeedsRebuild(new[] { a, b }, new[] { b, a }, true, true));
    }
}
