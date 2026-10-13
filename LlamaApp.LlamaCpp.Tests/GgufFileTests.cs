using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the .gguf path helpers behind the "Open With" flow. Windows
/// filenames are case-insensitive, so <c>MODEL.GGUF</c> must be recognized too
/// (the importer normalizes the name it lands on — llama.cpp's models-dir scan
/// is case-sensitive and would skip the upper-case original).
/// </summary>
public sealed class GgufFileTests
{
    [Theory]
    [InlineData("C:\\models\\llama-8b.gguf")]
    [InlineData("C:\\models\\llama-8b.GGUF")]
    [InlineData("C:\\models\\llama-8b.Gguf")]
    [InlineData("/home/u/mistral.gguf")]
    [InlineData("model.gguf")] // relative
    [InlineData("C:\\models\\deep.path.v2-Q4_K_M.gguf")]
    public void IsGguf_AcceptsAnyCasing(string path)
    {
        Assert.True(GgufFile.IsGguf(path));
    }

    [Theory]
    [InlineData("llama-8b.gguf.part")]
    [InlineData("llama-8b.bin")]
    [InlineData("gguf")]              // a file literally named "gguf"
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C:\\models\\readme.md")]
    public void IsGguf_RejectsNonGgufPaths(string? path)
    {
        Assert.False(GgufFile.IsGguf(path));
    }

    [Fact]
    public void FirstGgufPath_PicksTheFirstGguf_AndSkipsOthers()
    {
        // A file activation can carry several files; the first .gguf wins.
        Assert.Equal(
            "C:\\tmp\\b.gguf",
            GgufFile.FirstGgufPath(["C:\\tmp\\a.txt", "C:\\tmp\\b.gguf", "C:\\tmp\\c.gguf"]));
    }

    [Fact]
    public void FirstGgufPath_NonePresent_YieldsNull()
    {
        Assert.Null(GgufFile.FirstGgufPath(["C:\\tmp\\a.txt", null, ""]));
    }

    [Fact]
    public void FirstGgufPath_EmptyInput_YieldsNull()
    {
        Assert.Null(GgufFile.FirstGgufPath([]));
    }
}
