using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for <see cref="LlamaManager.FindModelFilePath(string, IReadOnlyDictionary{string, string}, IReadOnlyList{string}, string)"/>
/// — the rows' "reveal in Explorer" resolution: the enrolled-folder scan map
/// first (stem ids and shards the cache layouts don't cover), then the cache
/// layouts via <see cref="ModelFileLocator"/>, then the managed models dir's
/// direct <c>{id}.gguf</c>. Ids come from remote payloads, so path safety is
/// part of the contract.
/// </summary>
public sealed class ModelFilePathResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "llama-resolve-" + Guid.NewGuid().ToString("N"));
    private readonly string _managed;

    public ModelFilePathResolutionTests()
    {
        Directory.CreateDirectory(_root);
        _managed = Path.Combine(_root, "managed");
        Directory.CreateDirectory(_managed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string? Resolve(string id, IReadOnlyDictionary<string, string>? enrolled = null, IReadOnlyList<string>? cacheRoots = null)
        => LlamaManager.FindModelFilePath(
            id,
            enrolled ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            cacheRoots ?? [],
            _managed);

    [Fact]
    public void The_Enrolled_Scan_Map_Resolves_Stem_Ids()
    {
        var file = Path.Combine(_root, "enrolled", "MyModel-Q4_0.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "1");

        var resolved = Resolve(
            "MyModel-Q4_0",
            new Dictionary<string, string> { ["MyModel-Q4_0"] = file });

        Assert.Equal(file, resolved);
    }

    [Fact]
    public void An_Enrolled_Entry_For_A_Deleted_File_Falls_Through()
    {
        var gone = Path.Combine(_root, "enrolled", "gone.gguf"); // never created

        var resolved = Resolve(
            "gone",
            new Dictionary<string, string> { ["gone"] = gone });

        Assert.Null(resolved);
    }

    [Fact]
    public void The_Scan_Map_Wins_Even_When_The_Cache_Also_Matches()
    {
        var enrolledFile = Path.Combine(_root, "enrolled", "MyModel-Q4_0.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(enrolledFile)!);
        File.WriteAllText(enrolledFile, "1");

        var resolved = Resolve(
            "MyModel-Q4_0",
            new Dictionary<string, string> { ["MyModel-Q4_0"] = enrolledFile });

        Assert.Equal(enrolledFile, resolved);
    }

    [Fact]
    public void A_Managed_Dir_Import_Resolves_By_Stem()
    {
        var file = Path.Combine(_managed, "ImportedModel-Q8_0.gguf");
        File.WriteAllText(file, "1");

        Assert.Equal(file, Resolve("ImportedModel-Q8_0"));
    }

    [Fact]
    public void An_Unknown_Model_Resolves_To_Null()
        => Assert.Null(Resolve("org/repo-GGUF:Q4_K_M"));

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("owner/../../evil:Q4_K_M")]
    [InlineData("sub/dir/model")]
    public void Unsafe_Ids_Never_Reach_The_Disk(string id)
    {
        // Whatever exists on disk, a traversal-shaped id must resolve to null.
        var file = Path.Combine(_managed, "evil.gguf");
        File.WriteAllText(file, "1");

        Assert.Null(Resolve(id));
    }
}
