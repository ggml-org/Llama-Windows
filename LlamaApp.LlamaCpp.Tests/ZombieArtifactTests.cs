using LlamaApp.Common;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the zombie-artifact handling behind "imported-dir"-style ghosts:
/// a directory-import fixture (2-byte .gguf stubs) in the managed models dir
/// is listed by the router's scan, can never load, and — since the server's
/// DELETE chokes on it — reappears on every rescan. The rules under test:
/// fixtures resolve to nothing (so the ghost fallback engages) and only
/// fixtures are ever deletable — anything with real GGUF content is a model.
/// </summary>
public sealed class ZombieArtifactTests : IDisposable
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "llama-zombie-" + Guid.NewGuid().ToString("N"));
    private readonly string _managed;

    public ZombieArtifactTests()
    {
        _managed = Path.Combine(_root, "models");
        Directory.CreateDirectory(_managed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static void WriteFakeGguf(string path)
        => File.WriteAllText(path, "x"); // 1-byte stub, no GGUF magic

    private static void WriteRealGguf(string path)
        => File.WriteAllBytes(path, [.. GgufMagic, 0, 0, 0, 0]);

    [Fact]
    public void Magic_Detects_Real_Gguf_Content()
    {
        var real = Path.Combine(_root, "real.gguf");
        var fake = Path.Combine(_root, "fake.gguf");
        var empty = Path.Combine(_root, "empty.gguf");
        WriteRealGguf(real);
        WriteFakeGguf(fake);
        File.WriteAllText(empty, "");

        Assert.True(GgufFile.HasGgufMagic(real));
        Assert.False(GgufFile.HasGgufMagic(fake));
        Assert.False(GgufFile.HasGgufMagic(empty));
        Assert.False(GgufFile.HasGgufMagic(Path.Combine(_root, "missing.gguf")));
    }

    [Fact]
    public void A_Fixture_Directory_Resolves_To_Nothing()
    {
        // The user's actual zombie: imported-dir/{model,mmproj-model}.gguf,
        // both 2-byte stubs.
        var dir = Path.Combine(_managed, "imported-dir");
        Directory.CreateDirectory(dir);
        WriteFakeGguf(Path.Combine(dir, "model.gguf"));
        WriteFakeGguf(Path.Combine(dir, "mmproj-model.gguf"));

        Assert.Null(LlamaManager.FindModelFilePath(
            "imported-dir", new Dictionary<string, string>(), [], _managed));
    }

    [Fact]
    public void A_Real_Directory_Import_Resolves_To_Its_Main_File()
    {
        var dir = Path.Combine(_managed, "my-model");
        Directory.CreateDirectory(dir);
        WriteFakeGguf(Path.Combine(dir, "unrelated.gguf")); // sorts first but is a stub
        WriteRealGguf(Path.Combine(dir, "mmproj-model.gguf")); // real, but a sidecar
        var main = Path.Combine(dir, "model.gguf");
        WriteRealGguf(main);

        Assert.Equal(main, LlamaManager.FindModelFilePath(
            "my-model", new Dictionary<string, string>(), [], _managed));
    }

    [Fact]
    public void A_Fixture_Directory_Is_Removed()
    {
        var dir = Path.Combine(_managed, "imported-dir");
        Directory.CreateDirectory(dir);
        WriteFakeGguf(Path.Combine(dir, "model.gguf"));

        Assert.True(LlamaManager.RemoveManagedModelArtifact("imported-dir", _managed));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void A_Fixture_File_Is_Removed()
    {
        var stub = Path.Combine(_managed, "stub-model.gguf");
        WriteFakeGguf(stub);

        Assert.True(LlamaManager.RemoveManagedModelArtifact("stub-model", _managed));
        Assert.False(File.Exists(stub));
    }

    [Fact]
    public void Anything_With_Real_Gguf_Content_Is_Never_Removed()
    {
        var dir = Path.Combine(_managed, "my-model");
        Directory.CreateDirectory(dir);
        WriteRealGguf(Path.Combine(dir, "model.gguf"));

        Assert.False(LlamaManager.RemoveManagedModelArtifact("my-model", _managed));
        Assert.True(Directory.Exists(dir));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("na*me")]
    [InlineData("")]
    public void Unsafe_Ids_Are_Refused(string id)
    {
        // Whatever exists on disk, a traversal- or wildcard-shaped id must
        // never turn into a delete.
        Assert.False(LlamaManager.RemoveManagedModelArtifact(id, _managed));
    }

    [Fact]
    public void Entries_Outside_The_Managed_Dir_Are_Never_Touched()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        WriteFakeGguf(Path.Combine(outside, "victim.gguf"));

        // "elsewhere" exists as a sibling of the managed dir, not inside it —
        // the containment check must refuse even though the name resolves.
        Assert.False(LlamaManager.RemoveManagedModelArtifact("elsewhere", Path.Combine(_root, "models")));
        Assert.True(Directory.Exists(outside));
    }
}
