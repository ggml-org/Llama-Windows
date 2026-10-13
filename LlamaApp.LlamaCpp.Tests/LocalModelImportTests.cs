using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the "Open With" importer. The llama router only loads models in
/// its registry (HF-cache repos or <c>--models-dir</c> files — an arbitrary
/// path answers 404 File Not Found), so an opened .gguf must first land in the
/// app-managed models dir. The import must be cheap on re-open (hard link, no
/// 40 GB copy), tolerate upper-case extensions, and replace stale imports.
/// </summary>
public sealed class LocalModelImportTests : IDisposable
{
    private readonly string _root;

    public LocalModelImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-openwith-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteSource(string name, int size = 512, DateTime? lastWrite = null)
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        if (lastWrite is { } t)
            File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    // ---- DeriveModelId: mirrors llama.cpp's models-dir scan ----

    [Theory]
    [InlineData("llama-8b.gguf", "llama-8b")]
    [InlineData("MyModel-Q4_0.gguf", "MyModel-Q4_0")]  // no repo:quant split for local files
    [InlineData("deep.name.v2.gguf", "deep.name.v2")]
    [InlineData("MODEL.GGUF", "MODEL")]                // upper-case stem survives
    public void DeriveModelId_IsTheFileNameStem(string fileName, string expected)
    {
        Assert.Equal(expected, LocalModelImport.DeriveModelId(Path.Combine(_root, "x", fileName)));
    }

    [Fact]
    public void DeriveModelId_RejectsANamelessFile()
    {
        // A file literally named ".gguf" has no stem — no id to load by.
        Assert.Throws<ArgumentException>(() =>
            LocalModelImport.DeriveModelId(Path.Combine(_root, "x", ".gguf")));
    }

    // ---- Import ----

    [Fact]
    public void Import_HardLinksOnTheSameVolume_AndNormalizesTheExtension()
    {
        // Upper-case .GGUF: llama.cpp's scan is case-sensitive, so the landed
        // name must end in lower-case .gguf.
        var source = WriteSource("SOLVED.GGUF");
        var dir = Path.Combine(_root, "models");

        var result = LocalModelImport.Import(source, dir);

        Assert.Equal(LocalModelImportOutcome.Linked, result.Outcome);
        Assert.Equal("SOLVED", result.ModelId);
        Assert.Equal(Path.Combine(dir, "SOLVED.gguf"), result.TargetPath);
        Assert.True(File.Exists(result.TargetPath));
        Assert.False(result.ReplacedExisting);
        // A hard link shares content: read it back through the target.
        var viaTarget = File.ReadAllBytes(result.TargetPath);
        Assert.Equal(512, viaTarget.Length);
        Assert.Equal(new byte[512], viaTarget);
    }

    [Fact]
    public void Import_ReopenOfTheSameFile_IsANoOp()
    {
        var source = WriteSource("model.gguf");
        var dir = Path.Combine(_root, "models");
        LocalModelImport.Import(source, dir);

        var second = LocalModelImport.Import(source, dir);

        Assert.Equal(LocalModelImportOutcome.AlreadyPresent, second.Outcome);
        Assert.False(second.ReplacedExisting);
    }

    [Fact]
    public void Import_ReplacesAStaleTargetWithTheSameName()
    {
        var source = WriteSource("model.gguf", size: 512);
        var dir = Path.Combine(_root, "models");

        // A stale import with the same name from an older/other file (e.g. a
        // cross-volume copy whose source has since been replaced).
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.gguf"), new byte[16]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "model.gguf"), DateTime.UtcNow.AddDays(-3));

        var result = LocalModelImport.Import(source, dir);

        Assert.True(result.ReplacedExisting);
        Assert.Equal(512, new FileInfo(result.TargetPath).Length);
    }

    [Fact]
    public void Import_HardLinkSharesContent_EditsThroughEitherNameAreVisible()
    {
        // A hard link IS the opened file — not a snapshot. Later edits to the
        // source are automatically the import's content (and re-open needs no
        // re-import: size + last-write time agree through both names).
        var source = WriteSource("model.gguf");
        var dir = Path.Combine(_root, "models");
        var first = LocalModelImport.Import(source, dir);
        Assert.Equal(LocalModelImportOutcome.Linked, first.Outcome);

        File.WriteAllBytes(source, new byte[77]);

        Assert.Equal(77, new FileInfo(first.TargetPath).Length);
        Assert.Equal(LocalModelImportOutcome.AlreadyPresent, LocalModelImport.Import(source, dir).Outcome);
    }

    [Fact]
    public void Import_OfAFileAlreadyInTheModelsDir_IsANoOp()
    {
        var dir = Path.Combine(_root, "models");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "already-here.gguf");
        File.WriteAllBytes(path, new byte[64]);

        var result = LocalModelImport.Import(path, dir);

        Assert.Equal(LocalModelImportOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal("already-here", result.ModelId);
    }

    [Fact]
    public void Import_CreatesTheModelsDir_WhenMissing()
    {
        var source = WriteSource("model.gguf");
        var dir = Path.Combine(_root, "fresh-models");

        var result = LocalModelImport.Import(source, dir);

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(result.TargetPath));
    }

    [Fact]
    public void Import_MissingSource_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            LocalModelImport.Import(Path.Combine(_root, "nope.gguf"), Path.Combine(_root, "models")));
    }

    [Fact]
    public void ManagedModelsDir_IsUnderTheAppDataRoot()
    {
        Assert.Equal(
            Path.Combine(Common.AppData.Root, "models"),
            LlamaManager.ManagedModelsDir);
    }
}
