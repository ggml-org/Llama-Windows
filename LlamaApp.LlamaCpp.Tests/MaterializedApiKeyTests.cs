using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the materialized API-key file cleanup: the plaintext copy that
/// carries the key to the server via --api-key-file exists only for the
/// launch window (llama-server parses it into memory at startup and never
/// re-reads it — verified live). The removal is content-checked so only OUR
/// copy is deleted, and must be total — a missing, locked or foreign file is
/// a silent no-op that never faults a launch.
/// </summary>
public sealed class MaterializedApiKeyTests : IDisposable
{
    private readonly string _root;

    public MaterializedApiKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-apikey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string KeyFile => Path.Combine(_root, ".llama.apikey");

    [Fact]
    public void MatchingKey_IsRemoved()
    {
        File.WriteAllText(KeyFile, "sk_secret_value");

        LlamaManager.DeleteMaterializedApiKeyFile(KeyFile, "sk_secret_value");

        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void TrailingWhitespace_Tolerated()
    {
        // The server trims the file content when reading; so does the check.
        File.WriteAllText(KeyFile, "sk_secret_value\n\r\n");

        LlamaManager.DeleteMaterializedApiKeyFile(KeyFile, "sk_secret_value");

        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void ForeignContent_IsLeftAlone()
    {
        // A same-named file repurposed by something else must survive.
        File.WriteAllText(KeyFile, "something_else_entirely");

        LlamaManager.DeleteMaterializedApiKeyFile(KeyFile, "sk_secret_value");

        Assert.True(File.Exists(KeyFile));
    }

    [Theory]
    [InlineData(null, "sk_value")]   // no file was materialized (loopback bind)
    [InlineData("", "sk_value")]
    [InlineData("key.txt", null)]    // no key configured
    [InlineData("key.txt", "")]
    public void NoOpInputs_AreSilent(string? path, string? key)
    {
        LlamaManager.DeleteMaterializedApiKeyFile(path, key); // must not throw
    }

    [Fact]
    public void MissingFile_IsSilent()
    {
        LlamaManager.DeleteMaterializedApiKeyFile(KeyFile, "sk_value"); // must not throw
    }
}
