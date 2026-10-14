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
    public void ManagedModelsDir_IsWhereTheOutOfPackageServerSeesIt()
    {
        // NOT %LOCALAPPDATA%\Llama: MSIX virtualization redirects the app's
        // writes there, while the server (outside the package identity)
        // scans the real path — the API-key-file instance of this trap
        // is issue #107. The user profile is never virtualized.
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".llama", "models"),
            LlamaManager.ManagedModelsDir);
        Assert.DoesNotContain("LocalAppData", LlamaManager.ManagedModelsDir);
    }

    // ---- Sidecars: mmproj / draft / shards (llama.cpp models-dir rules) ----

    private string Sidecar(string name, int size = 128)
    {
        var dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void CollectModelFiles_PicksUpTheMmprojSidecar()
    {
        var main = WriteSource("Vision-8B-Q4_K_M.gguf");
        var mmproj = Sidecar("mmproj-Vision-8B-F16.gguf");

        var files = LocalModelImport.CollectModelFiles(main);

        Assert.Contains(main, files);
        Assert.Contains(mmproj, files);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void CollectModelFiles_PrefersTheQuantMatchingMmproj()
    {
        var main = WriteSource("Vision-8B-Q4_K_M.gguf");
        var mismatched = Sidecar("mmproj-Vision-8B-Q8_0.gguf");
        var matched = Sidecar("mmproj-Vision-8B-Q4_K_M.gguf");

        var files = LocalModelImport.CollectModelFiles(main);

        Assert.Contains(matched, files);
        Assert.DoesNotContain(mismatched, files);
    }

    [Fact]
    public void CollectModelFiles_KeepsOneDraftSidecar()
    {
        var main = WriteSource("model.gguf");
        var draft = Sidecar("mtp-model.gguf");

        var files = LocalModelImport.CollectModelFiles(main);

        Assert.Equal(new[] { main, draft }, files);
    }

    [Fact]
    public void CollectModelFiles_CollectsShardSiblings()
    {
        var shard1 = WriteSource("Big-00001-of-00002.gguf");
        var shard2 = Sidecar("Big-00002-of-00002.gguf");
        Sidecar("unrelated.gguf");

        var files = LocalModelImport.CollectModelFiles(shard1);

        Assert.Contains(shard1, files);
        Assert.Contains(shard2, files);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void CollectModelFiles_AloneModel_ImportsJustItself()
    {
        var main = WriteSource("lonewolf.gguf");

        Assert.Equal(new[] { main }, LocalModelImport.CollectModelFiles(main));
    }

    [Fact]
    public void Import_WithSidecars_UsesTheDirectoryLayout()
    {
        var main = WriteSource("Vision-8B-Q4_K_M.gguf");
        var mmproj = Sidecar("mmproj-Vision-8B-F16.gguf");
        var dir = Path.Combine(_root, "models");

        var result = LocalModelImport.Import(main, dir);

        Assert.Equal("Vision-8B-Q4_K_M", result.ModelId);
        Assert.Equal(2, result.FileCount);
        var modelDir = Path.Combine(dir, "Vision-8B-Q4_K_M");
        Assert.True(File.Exists(Path.Combine(modelDir, "Vision-8B-Q4_K_M.gguf")));
        Assert.True(File.Exists(Path.Combine(modelDir, "mmproj-Vision-8B-F16.gguf")));
        // Top level stays clean — a directory import must not also drop a file.
        Assert.False(File.Exists(Path.Combine(dir, "Vision-8B-Q4_K_M.gguf")));
    }

    [Fact]
    public void Import_WithSidecars_ReopenIsANoOp()
    {
        var main = WriteSource("Vision-8B-Q4_K_M.gguf");
        Sidecar("mmproj-Vision-8B-F16.gguf");
        var dir = Path.Combine(_root, "models");
        LocalModelImport.Import(main, dir);

        var second = LocalModelImport.Import(main, dir);

        Assert.Equal(LocalModelImportOutcome.AlreadyPresent, second.Outcome);
        Assert.Equal(2, second.FileCount);
    }

    [Fact]
    public void Import_MmprojOpenedDirectly_ResolvesToTheSiblingModel()
    {
        WriteSource("Vision-8B-Q4_K_M.gguf");
        var mmproj = Sidecar("mmproj-Vision-8B-F16.gguf");
        var dir = Path.Combine(_root, "models");

        var result = LocalModelImport.Import(mmproj, dir);

        Assert.Equal("Vision-8B-Q4_K_M", result.ModelId);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public void Import_MmprojWithNoSiblingModel_FailsWithAGuidingMessage()
    {
        var mmproj = Sidecar("mmproj-lonely-F16.gguf");

        var ex = Assert.Throws<IOException>(() =>
            LocalModelImport.Import(mmproj, Path.Combine(_root, "models")));

        Assert.Contains("projector sidecar", ex.Message);
        Assert.Contains("Open the model file", ex.Message);
    }

    [Theory]
    [InlineData("Big-00001-of-00002.gguf", "Big")]
    [InlineData("Big-00007-of-00003.gguf", "Big")]
    [InlineData("plain.gguf", "plain")]
    [InlineData("Qwen-Coder-00001-of-00004.gguf", "Qwen-Coder")]
    public void DeriveModelId_StripsShardSuffixes(string fileName, string expected)
    {
        Assert.Equal(expected, LocalModelImport.DeriveModelId(Path.Combine(_root, "x", fileName)));
    }

    [Theory]
    [InlineData("Vision-8B-Q4_K_M", "Q4_K_M")]
    [InlineData("Vision-8B-BF16", "BF16")]
    [InlineData("model-Q8_0", "Q8_0")]
    [InlineData("Vision-8B-Instruct", null)]  // not a quant tag
    [InlineData("model", null)]
    [InlineData("a-IQ4_NL", "IQ4_NL")]
    public void ExtractQuantTag_PicksTheTrailingQuant(string stem, string? expected)
    {
        Assert.Equal(expected, LocalModelImport.ExtractQuantTag(stem));
    }
}
