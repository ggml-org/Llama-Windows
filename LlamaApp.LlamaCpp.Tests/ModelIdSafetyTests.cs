using System.Text.Json;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Security tests for the two model-id sinks that take remote data:
/// <see cref="ModelFileLocator"/> (ids become filesystem paths) and
/// <see cref="LlamaManager.BuildModelRequestBody"/> (ids become JSON bodies).
/// Ids can originate in the llama.app catalog, Hub search results, or an
/// adopted server's <c>/models</c> payload, so neither may trust them raw.
/// </summary>
public sealed class ModelIdSafetyTests : IDisposable
{
    private readonly string _root;

    public ModelIdSafetyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-idsafety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- IsSafeModelId ----

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("owner/repo:Q4_K_M")]
    [InlineData("repo")]
    [InlineData("repo:q8_0")]
    [InlineData("TheBloke/Mistral-7B-Instruct-v0.2-GGUF:Q5_K_M")]
    [InlineData("unsloth/Qwen2.5-7B-Instruct-GGUF:IQ4_NL")]
    [InlineData("org/repo.name_v2-extra:Q4_0")]
    public void SafeIds_AreAccepted(string id)
    {
        Assert.True(ModelFileLocator.IsSafeModelId(id));
    }

    [Theory]
    [InlineData("..\\..\\..\\evil:Q4_K_M")]
    [InlineData("..\\..\\evil")]
    [InlineData("owner/../../etc:Q4_K_M")]
    [InlineData("../repo:Q4_K_M")]
    [InlineData("owner/repo/extra:Q4_K_M")]      // more than one slash
    [InlineData("owner\\repo:Q4_K_M")]           // backslash separator
    [InlineData("owner/repo:..")]                // traversal quant
    [InlineData("owner/repo:")]
    [InlineData("/")]
    [InlineData("owner/repo:*")]                 // wildcard
    [InlineData("owner/repo:Q4?")]
    [InlineData("owner/repo:Q4\u0000")]
    [InlineData("C:/Windows/System32:Q4_K_M")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnsafeIds_AreRejected(string? id)
    {
        Assert.False(ModelFileLocator.IsSafeModelId(id));
    }

    [Fact]
    public void TraversalId_DoesNotEscapeCacheRoot()
    {
        // Plant a GGUF one level ABOVE the cache root, then attempt the
        // classic backslash traversal. Without validation the id would
        // resolve out of the root and find it.
        var parent = Path.GetDirectoryName(_root)!;
        var outsideName = "llama-idsafety-outside-" + Guid.NewGuid().ToString("N") + ".gguf";
        var outside = Path.Combine(parent, outsideName);
        File.WriteAllBytes(outside, new byte[16]);
        try
        {
            // The id must be rejected outright, so no probing happens at all.
            Assert.False(ModelFileLocator.IsSafeModelId("..\\..\\evil"));
            Assert.Null(ModelFileLocator.TryFind("..\\..\\evil:Q4_K_M", [_root]));
            Assert.Null(ModelFileLocator.TryFind("owner/../../evil:Q4_K_M", [_root]));
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void IsUnder_AcceptsRootAndDescendants_RejectsSiblings()
    {
        Assert.True(ModelFileLocator.IsUnder(_root, _root));
        Assert.True(ModelFileLocator.IsUnder(_root, Path.Combine(_root, "a", "b")));
        Assert.False(ModelFileLocator.IsUnder(_root, Path.Combine(_root, "..", "sibling")));
        Assert.False(ModelFileLocator.IsUnder(_root, Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void ValidId_StillResolvesNormally()
    {
        var dir = Path.Combine(_root, "models--owner--repo", "snapshots", "sha");
        Directory.CreateDirectory(dir);
        var gguf = Path.Combine(dir, "repo-Q4_K_M.gguf");
        File.WriteAllBytes(gguf, new byte[42]);

        var found = ModelFileLocator.TryFind("owner/repo:Q4_K_M", [_root]);

        Assert.NotNull(found);
        Assert.Equal(gguf, found!.PrimaryFilePath);
        Assert.Equal(42, found.TotalSizeBytes);
    }

    // ---- BuildModelRequestBody ----

    [Theory]
    [InlineData("owner/repo:Q4_K_M")]
    [InlineData("weird\"quote")]
    [InlineData("back\\slash")]
    [InlineData("line\nbreak\ttab")]
    [InlineData("dollar$(whoami)")]
    [InlineData("trailing\\")]
    public void ModelRequestBody_IsValidJson_WithExactId(string id)
    {
        var json = LlamaManager.BuildModelRequestBody(id);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(id, doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("ctx_size", out _));
    }

    [Fact]
    public void ModelRequestBody_IncludesCtxSize_WhenProvided()
    {
        var json = LlamaManager.BuildModelRequestBody("owner/repo:Q4_K_M", 8192);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("owner/repo:Q4_K_M", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(8192, doc.RootElement.GetProperty("ctx_size").GetInt32());
    }

    [Fact]
    public void ModelRequestBody_QuoteInId_CannotInjectFields()
    {
        // A raw-interpolated body would let this append "ctx_size":1 and
        // change the request; escaping must keep it a single model value.
        var id = "repo\",\"ctx_size\":1,\"x\":\"";
        var json = LlamaManager.BuildModelRequestBody(id);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(id, doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("x", out _));
        Assert.False(doc.RootElement.TryGetProperty("ctx_size", out _));
    }
}
