using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="ExplorerReveal"/> — the "show model file in
/// Explorer" launch decision and the explorer.exe argument building.
/// Explorer's argument parsing is non-standard, and the path may come from a
/// server report, so the quoting/refusal rules are pinned here.
/// </summary>
public class ExplorerRevealTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "llama-reveal-" + Guid.NewGuid().ToString("N"));

    public ExplorerRevealTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Null_Or_Blank_Paths_Cannot_Be_Revealed()
    {
        Assert.False(ExplorerReveal.CanReveal(null));
        Assert.False(ExplorerReveal.CanReveal(""));
        Assert.False(ExplorerReveal.CanReveal("   "));
        Assert.Null(ExplorerReveal.BuildReveal(null));
        Assert.Null(ExplorerReveal.BuildReveal("  "));
    }

    [Fact]
    public void An_Existing_File_Reveals_With_Select()
    {
        var file = Path.Combine(_root, "model.gguf");
        File.WriteAllText(file, "1");

        var reveal = ExplorerReveal.BuildReveal(file);

        Assert.NotNull(reveal);
        Assert.Equal("explorer.exe", reveal!.Value.FileName);
        Assert.Equal("/select,\"" + file + "\"", reveal.Value.Arguments);
    }

    [Fact]
    public void A_Deleted_File_Falls_Back_To_Its_Folder()
    {
        var file = Path.Combine(_root, "gone.gguf"); // never created

        var reveal = ExplorerReveal.BuildReveal(file);

        Assert.NotNull(reveal);
        Assert.Equal(_root, reveal!.Value.FileName);
        Assert.Equal("", reveal.Value.Arguments);
    }

    [Fact]
    public void A_Missing_Everything_Reveals_Nothing()
        => Assert.Null(ExplorerReveal.BuildReveal(
            Path.Combine(_root, "not-there", "also-gone.gguf")));

    [Fact]
    public void A_Quote_In_The_Path_Is_Refused()
        => Assert.Null(ExplorerReveal.BuildReveal(_root + "we\\ird\"name.gguf"));
}
