using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for <see cref="ModelFileLocator"/> — the fallback that finds a
/// model's GGUF on disk when the server reports no path (the router /models
/// payload has none, so without it the details view can never read context
/// metadata). The locator probes two real-world cache layouts; each test
/// builds a tiny fake cache in a temp dir — no multi-GB fixtures.
/// </summary>
public sealed class ModelFileLocatorTests : IDisposable
{
    private readonly string _root;

    public ModelFileLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string HubFile(string repo, string snapshot, string fileName, int size = 100)
    {
        var dir = Path.Combine(_root, "models--" + repo.Replace("/", "--"), "snapshots", snapshot);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string FlatFile(string repo, string fileName, int size = 100)
    {
        var path = Path.Combine(_root, repo.Replace('/', '_') + "_" + fileName);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private ModelFileLocator.LocatedModel? Find(string id) =>
        ModelFileLocator.TryFind(id, [_root]);

    [Fact]
    public void FindsModelInHubCacheLayout()
    {
        var expected = HubFile("mistralai/Ministral-3-3B-Reasoning-2512-GGUF", "abc123",
            "Ministral-3-3B-Reasoning-2512-Q4_K_M.gguf", size: 1234);

        var found = Find("mistralai/Ministral-3-3B-Reasoning-2512-GGUF:Q4_K_M");

        Assert.NotNull(found);
        Assert.Equal(expected, found!.PrimaryFilePath);
        Assert.Equal(1234, found.TotalSizeBytes);
    }

    [Fact]
    public void FindsModelInFlatLlamaCppCacheLayout()
    {
        var expected = FlatFile("ggml-org/gemma-3-270m-it-qat-GGUF",
            "gemma-3-270m-it-qat-Q4_0.gguf", size: 777);

        var found = Find("ggml-org/gemma-3-270m-it-qat-GGUF:Q4_0");

        Assert.NotNull(found);
        Assert.Equal(expected, found!.PrimaryFilePath);
        Assert.Equal(777, found.TotalSizeBytes);
    }

    [Fact]
    public void QuantSelectsAmongTheReposFiles()
    {
        HubFile("org/model-GGUF", "sha", "model-Q4_K_M.gguf");
        var q8 = HubFile("org/model-GGUF", "sha", "model-Q8_0.gguf");

        var found = Find("org/model-GGUF:Q8_0");

        Assert.NotNull(found);
        Assert.Equal(q8, found!.PrimaryFilePath);
    }

    [Fact]
    public void MmprojVisionProjectorNeverMatches()
    {
        HubFile("org/vision-GGUF", "sha", "vision-BF16-mmproj.gguf");

        Assert.Null(Find("org/vision-GGUF:BF16"));
    }

    [Fact]
    public void MultiShardModelSumsAllShardsAndReadsTheFirst()
    {
        var shard1 = HubFile("org/big-GGUF", "sha", "big-Q4_K_M-00001-of-00002.gguf", size: 100);
        HubFile("org/big-GGUF", "sha", "big-Q4_K_M-00002-of-00002.gguf", size: 50);

        var found = Find("org/big-GGUF:Q4_K_M");

        Assert.NotNull(found);
        Assert.Equal(shard1, found!.PrimaryFilePath); // the header lives in shard 1
        Assert.Equal(150, found.TotalSizeBytes);
    }

    [Fact]
    public void BareRepoIdMatchesAnyModelFile()
    {
        // Mid-download the server ids the model by its bare repo (quant unresolved).
        var expected = HubFile("org/model-GGUF", "sha", "model-Q4_K_M.gguf");

        var found = Find("org/model-GGUF");

        Assert.NotNull(found);
        Assert.Equal(expected, found!.PrimaryFilePath);
    }

    [Fact]
    public void UnknownModelReturnsNull()
    {
        HubFile("org/present-GGUF", "sha", "present-Q4_K_M.gguf");

        Assert.Null(Find("org/absent-GGUF:Q4_K_M"));
        Assert.Null(Find("org/present-GGUF:Q8_0")); // right repo, wrong quant
    }

    [Fact]
    public void MissingDirectoriesAreSkippedNotThrown()
    {
        // No cache content at all — just must not throw.
        Assert.Null(Find("org/model-GGUF:Q4_K_M"));
        Assert.Null(ModelFileLocator.TryFind("org/model-GGUF:Q4_K_M",
            [Path.Combine(_root, "does-not-exist")]));
    }

    [Fact]
    public void GarbageIdsReturnNull()
    {
        Assert.Null(Find(""));
        Assert.Null(Find("   "));
        Assert.Null(Find(":Q4_K_M"));
    }
}

