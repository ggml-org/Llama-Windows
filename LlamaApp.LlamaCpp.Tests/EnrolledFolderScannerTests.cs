using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the enrolled model-folder scan (Settings → General → Model
/// folders): users point the app at folders of GGUF models they already
/// have; the scan finds them recursively and the renderer turns each into a
/// <c>model = path</c> section of the <c>--models-preset</c> INI the server
/// always launches with. The classification must mirror llama.cpp's own
/// models-dir scan (mmproj/draft sidecars, one shard set per model, several
/// quants per folder are several models), the scan must exclude the HF
/// cache (the server already lists it) and survive missing/odd folders, and
/// duplicate stems must disambiguate deterministically.
/// </summary>
public sealed class EnrolledFolderScannerTests : IDisposable
{
    private readonly string _root;

    public EnrolledFolderScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-modelfolders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteGguf(params string[] relativePath)
    {
        var path = Path.Combine(new[] { _root }.Concat(relativePath).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    // ---- discovery ----

    [Fact]
    public void Scan_FindsModelsRecursively_WithSidecarsAndShards()
    {
        // Two levels deep, mixed content:
        //  root/fam/model-Q4_0.gguf            (main)
        //  root/fam/model-Q8_0.gguf            (another quant = another model)
        //  root/fam/mmproj-model-Q4_0.gguf     (vision sidecar, quant-matched)
        //  root/fam/mtp-model.gguf             (draft sidecar)
        //  root/deep/nested/big-00001-of-00002.gguf, big-00002-of-00002.gguf
        var mainQ4 = WriteGguf("fam", "model-Q4_0.gguf");
        var mainQ8 = WriteGguf("fam", "model-Q8_0.gguf");
        var mmproj = WriteGguf("fam", "mmproj-model-Q4_0.gguf");
        var draft = WriteGguf("fam", "mtp-model.gguf");
        var shard1 = WriteGguf("deep", "nested", "big-00001-of-00002.gguf");
        WriteGguf("deep", "nested", "big-00002-of-00002.gguf");

        var models = EnrolledFolderScanner.Scan([_root]);

        Assert.Equal(3, models.Count);

        var q4 = Assert.Single(models, m => m.Name == "model-Q4_0");
        Assert.Equal(mainQ4, q4.ModelPath);
        Assert.Equal(mmproj, q4.MmprojPath);
        Assert.Equal(draft, q4.DraftPath);

        var q8 = Assert.Single(models, m => m.Name == "model-Q8_0");
        Assert.Equal(mainQ8, q8.ModelPath);
        // The only mmproj in the dir pairs with both quants (llama.cpp keeps
        // one per dir); the draft sidecar pairs with both too.
        Assert.Equal(mmproj, q8.MmprojPath);
        Assert.Equal(draft, q8.DraftPath);

        var shardModel = Assert.Single(models, m => m.Name == "big");
        Assert.Equal(shard1, shardModel.ModelPath);   // the FIRST shard stands for the set
        Assert.Null(shardModel.MmprojPath);
    }

    [Fact]
    public void Scan_SkipsMmprojAndDraftsAsModels()
    {
        WriteGguf("proj", "mmproj-model.gguf");
        WriteGguf("proj", "dflash-model.gguf");
        WriteGguf("proj", "dspark-model.gguf");

        var models = EnrolledFolderScanner.Scan([_root]);

        Assert.Empty(models); // sidecars alone are no model — nothing to load
    }

    [Fact]
    public void Scan_SkipsNonFirstShards()
    {
        // A set missing its first shard cannot load — don't list it.
        WriteGguf("s", "mid-00002-of-00003.gguf");
        WriteGguf("s", "mid-00003-of-00003.gguf");

        var models = EnrolledFolderScanner.Scan([_root]);

        Assert.Empty(models);
    }

    [Fact]
    public void Scan_ExcludesTheCacheAndManagedDirs()
    {
        var cache = Path.Combine(_root, "cache");
        var managed = Path.Combine(_root, "managed");
        WriteGguf("cache", "cached-model-Q4_0.gguf");
        WriteGguf("managed", "imported-model-Q4_0.gguf");
        var mine = WriteGguf("mine", "my-model-Q4_0.gguf");

        var models = EnrolledFolderScanner.Scan([_root], [cache, managed]);

        var model = Assert.Single(models);
        Assert.Equal("my-model-Q4_0", model.Name);
        Assert.Equal(mine, model.ModelPath);
    }

    [Fact]
    public void Scan_MissingFolder_IsSkippedQuietly()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var mine = WriteGguf("mine", "my-model.gguf");

        var models = EnrolledFolderScanner.Scan([missing, _root]);

        var model = Assert.Single(models);
        Assert.Equal(mine, model.ModelPath);
    }

    [Fact]
    public void Scan_DedupesAcrossOverlappingFolders()
    {
        var model = WriteGguf("fam", "model-Q4_0.gguf");
        var parent = _root; // contains fam/ — enrolled twice via nested roots

        var models = EnrolledFolderScanner.Scan([parent, Path.Combine(parent, "fam")]);

        var found = Assert.Single(models);
        Assert.Equal(model, found.ModelPath);
    }

    // ---- naming ----

    [Fact]
    public void DuplicateStems_GetParentPrefixedNames()
    {
        // Same file name in two enrolled folders: both are listed, both
        // loadable, deterministic names — parent prefix breaks the tie.
        var a = WriteGguf("alpha", "twin-Q4_0.gguf");
        var b = WriteGguf("beta", "twin-Q4_0.gguf");

        var models = EnrolledFolderScanner.Scan([Path.Combine(_root, "alpha"), Path.Combine(_root, "beta")]);

        Assert.Equal(2, models.Count);
        Assert.Equal(
            new[] { "alpha-twin-Q4_0", "beta-twin-Q4_0" },
            models.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { a, b }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            models.Select(m => m.ModelPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    // ---- rendering ----

    [Fact]
    public void RenderSections_PinsTheExactIniShape()
    {
        // The INI grammar (llama.cpp preset.cpp): keys are ident-shaped
        // ("model", "mmproj", "spec-draft-model"), values run to end of line,
        // sections accept anything but ']'. Paths are absolute.
        var models = new[]
        {
            new EnrolledFolderModel("fam-model-Q4_0",
                @"D:\models\fam\model-Q4_0.gguf",
                @"D:\models\fam\mmproj-model-Q4_0.gguf",
                @"D:\models\fam\mtp-model.gguf"),
            new EnrolledFolderModel("plain", @"E:\solo.gguf", null, null),
        };

        Assert.Equal(
            "[fam-model-Q4_0]\n" +
            "model = D:\\models\\fam\\model-Q4_0.gguf\n" +
            "mmproj = D:\\models\\fam\\mmproj-model-Q4_0.gguf\n" +
            "spec-draft-model = D:\\models\\fam\\mtp-model.gguf\n" +
            "\n" +
            "[plain]\n" +
            "model = E:\\solo.gguf\n" +
            "\n",
            EnrolledFolderScanner.RenderSections(models));
    }

    [Fact]
    public void RenderSections_EmptyForNoModels()
    {
        Assert.Equal("", EnrolledFolderScanner.RenderSections([]));
    }

    [Fact]
    public void ScanToRender_EndToEnd_MatchesAServeablePreset()
    {
        // The whole chain the server consumes: files on disk → INI text a
        // llama-server would parse (section, model key, sidecar keys).
        var main = WriteGguf("fam", "model-Q8_0.gguf");
        var mmproj = WriteGguf("fam", "mmproj-model-Q8_0.gguf");

        var ini = EnrolledFolderScanner.RenderSections(
            EnrolledFolderScanner.Scan([Path.Combine(_root, "fam")]));

        Assert.Equal(
            "[model-Q8_0]\n" +
            $"model = {main}\n" +
            $"mmproj = {mmproj}\n\n",
            ini);
    }
}
