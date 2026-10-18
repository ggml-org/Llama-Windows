using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the ghost guard (<see cref="LlamaManager.FilterExistingModels"/>):
/// a ctx / spec-type settings entry outlives its model (file deleted in
/// Explorer, folder un-enrolled while the app was off) and its preset section
/// makes the router re-list the model as a loadable ghost. The filter drops
/// such entries from the rendering — without ever touching the settings
/// themselves (a temporarily unavailable folder must get its preferences
/// back) and without dropping busy models (mid-download has no file yet).
/// </summary>
public sealed class GhostPresetFilterTests
{
    private static readonly Func<string, bool> NeverBusy = _ => false;

    [Fact]
    public void Entries_For_Existing_Models_Stay()
    {
        var entries = new Dictionary<string, int> { ["org/repo:Q4_0"] = 8192, ["mine-Q4_0"] = 4096 };

        var kept = LlamaManager.FilterExistingModels(entries, NeverBusy, _ => true);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Entries_For_Gone_Models_Are_Dropped()
    {
        var entries = new Dictionary<string, int> { ["ghost-Q4_0"] = 8192, ["org/repo:Q4_0"] = 4096 };

        var kept = LlamaManager.FilterExistingModels(
            entries, NeverBusy, id => id != "ghost-Q4_0");

        Assert.Single(kept);
        Assert.Equal("org/repo:Q4_0", kept[0].Key);
    }

    [Fact]
    public void Busy_Models_Keep_Their_Entries_Even_Without_A_File()
        // Mid-download / mid-load: no file on disk yet — dropping the entry
        // would silently lose the preference.
    {
        var entries = new Dictionary<string, int> { ["downloading-Q4_0"] = 16384 };

        var kept = LlamaManager.FilterExistingModels(
            entries, id => id == "downloading-Q4_0", _ => false);

        Assert.Single(kept);
    }

    [Fact]
    public void Spec_Entries_Get_The_Same_Guard()
    {
        var entries = new Dictionary<string, string>
        {
            ["ghost-Q4_0"] = "draft-mtp",
            ["live-Q4_0"] = "draft-dflash",
        };

        var kept = LlamaManager.FilterExistingModels(
            entries, NeverBusy, id => id == "live-Q4_0");

        Assert.Single(kept);
        Assert.Equal("live-Q4_0", kept[0].Key);
        Assert.Equal("draft-dflash", kept[0].Value);
    }

    [Fact]
    public void All_Gone_Renders_Nothing()
    {
        var kept = LlamaManager.FilterExistingModels(
            new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, NeverBusy, _ => false);

        Assert.Empty(kept);
    }
}
