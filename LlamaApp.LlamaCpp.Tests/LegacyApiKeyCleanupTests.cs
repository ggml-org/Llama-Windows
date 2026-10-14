using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the legacy plaintext API-key file sweep. The current design hands
/// the server its key via the LLAMA_API_KEY environment variable and persists
/// only the DPAPI-protected form in settings.json, so a ".llama.apikey" file is
/// pure residue from an earlier --api-key-file build and must be removed on
/// launch. Best-effort and total: a missing, locked or unwritable file is a
/// silent no-op that never faults a launch — and it sweeps only the one known
/// name, leaving every other file in the directory untouched.
/// </summary>
public sealed class LegacyApiKeyCleanupTests : IDisposable
{
    private readonly string _root;

    public LegacyApiKeyCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-legacykey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LegacyFile_IsRemoved()
    {
        var p = Path.Combine(_root, ".llama.apikey");
        File.WriteAllText(p, "sk_leftover_secret");

        LlamaManager.RemoveLegacyApiKeyFile(_root);

        Assert.False(File.Exists(p));
    }

    [Fact]
    public void MissingFile_IsSilent()
    {
        LlamaManager.RemoveLegacyApiKeyFile(_root); // must not throw
    }

    [Fact]
    public void UnrelatedFiles_AreLeftAlone()
    {
        var keep = Path.Combine(_root, "model-context-presets.ini");
        File.WriteAllText(keep, "keep me");

        LlamaManager.RemoveLegacyApiKeyFile(_root);

        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void MissingDirectory_IsSilent()
    {
        LlamaManager.RemoveLegacyApiKeyFile(Path.Combine(_root, "no-such-dir")); // must not throw
    }
}
