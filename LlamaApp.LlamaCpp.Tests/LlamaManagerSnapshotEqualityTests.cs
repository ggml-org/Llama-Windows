using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the supervisor poll's snapshot change detection
/// (<see cref="LlamaManager.SnapshotsEqual"/>). The gate decides when
/// <c>ModelsChanged</c> is published: a false "equal" would swallow a real
/// model/load-state change, so every observable field must be covered.
/// </summary>
public class LlamaManagerSnapshotEqualityTests
{
    private static LlamaManager.ServerModel Model(
        string id = "ggml-org/gemma-3-4b-it-GGUF:Q4_K_M",
        string? path = "/models/gemma.gguf",
        string status = "unloaded",
        string? source = "cache",
        bool canRemove = true,
        bool supportsImage = false)
        => new()
        {
            Id = id,
            Path = path,
            Status = status,
            Source = source,
            CanRemove = canRemove,
            SupportsImage = supportsImage,
        };

    [Fact]
    public void Null_Never_Equals_A_Snapshot()
        => Assert.False(LlamaManager.SnapshotsEqual(null, [Model()]));

    [Fact]
    public void Empty_Snapshots_Are_Equal()
        => Assert.True(LlamaManager.SnapshotsEqual([], []));

    [Fact]
    public void Identical_Snapshots_Are_Equal()
    {
        IReadOnlyList<LlamaManager.ServerModel> a = [Model(), Model(id: "org/other-GGUF:Q8_0", status: "loaded")];
        IReadOnlyList<LlamaManager.ServerModel> b = [Model(), Model(id: "org/other-GGUF:Q8_0", status: "loaded")];
        Assert.True(LlamaManager.SnapshotsEqual(a, b));
    }

    [Fact]
    public void Count_Mismatch_Is_A_Change()
    {
        IReadOnlyList<LlamaManager.ServerModel> a = [Model()];
        IReadOnlyList<LlamaManager.ServerModel> b = [Model(), Model(id: "org/other-GGUF:Q8_0")];
        Assert.False(LlamaManager.SnapshotsEqual(a, b));
    }

    [Theory]
    // Each observable field, changed one at a time — all must register as a change.
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("status")]
    [InlineData("source")]
    [InlineData("canRemove")]
    [InlineData("supportsImage")]
    public void Any_Observable_Field_Change_Is_A_Change(string field)
    {
        var changed = field switch
        {
            "id" => Model(id: "org/other-GGUF:Q8_0"),
            "path" => Model(path: "/models/elsewhere.gguf"),
            "status" => Model(status: "loaded"),
            "source" => Model(source: "models-dir"),
            "canRemove" => Model(canRemove: false),
            _ => Model(supportsImage: true),
        };

        IReadOnlyList<LlamaManager.ServerModel> a = [Model()];
        IReadOnlyList<LlamaManager.ServerModel> b = [changed];
        Assert.False(LlamaManager.SnapshotsEqual(a, b));
    }

    [Fact]
    public void Load_State_Flip_Is_A_Change()
    {
        // The exact transition the poll must surface: play glyph -> load ring.
        IReadOnlyList<LlamaManager.ServerModel> before = [Model(status: "unloaded")];
        IReadOnlyList<LlamaManager.ServerModel> after = [Model(status: "loading")];
        Assert.False(LlamaManager.SnapshotsEqual(before, after));
    }

    [Fact]
    public void Reorder_Is_Conservatively_A_Change()
    {
        // Order-sensitive by design: a reorder at worst costs one redundant
        // publish, never a missed change.
        IReadOnlyList<LlamaManager.ServerModel> a = [Model(), Model(id: "org/other-GGUF:Q8_0")];
        IReadOnlyList<LlamaManager.ServerModel> b = [Model(id: "org/other-GGUF:Q8_0"), Model()];
        Assert.False(LlamaManager.SnapshotsEqual(a, b));
    }
}
