using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the speculative-decoding draft-head detection (issue #97): a
/// repo ships heads as prefix-named sidecar GGUFs, and the app must pick the
/// best one and map it to the llama.cpp <c>--spec-type</c> token the router
/// preset carries — the whole chain (detection → preference → spec type →
/// INI) rests on these classification rules.
/// </summary>
public sealed class DraftHeadsTests
{
    // ---- sidecar detection ----

    [Fact]
    public void RepoShippingDflashAndMtp_PrefersDflash()
    {
        // Qwen3.8-27B's exact shape: dflash measured 35–50% faster than MTP
        // on it, and the maintainers recommend dflash whenever both ship.
        var files = new[]
        {
            "Qwen3.8-27B-Q4_0.gguf",
            "dflash-Qwen3.8-27B-Q4_0.gguf",
            "mtp-Qwen3.8-27B-Q4_0.gguf",
        };

        Assert.Equal(DraftHeadKind.Dflash, DraftHeads.DetectSidecar(files));
    }

    [Fact]
    public void RepoShippingDsparkOutranksDflash()
    {
        var files = new[]
        {
            "model-Q4_0.gguf",
            "dspark-model-Q4_0.gguf",
            "dflash-model-Q4_0.gguf",
        };

        Assert.Equal(DraftHeadKind.Dspark, DraftHeads.DetectSidecar(files));
    }

    [Fact]
    public void RepoShippingOnlyMtp_KeepsMtp()
    {
        var files = new[] { "model-Q4_0.gguf", "mtp-model-Q4_0.gguf" };

        Assert.Equal(DraftHeadKind.Mtp, DraftHeads.DetectSidecar(files));
    }

    [Fact]
    public void SidecarDetection_IsCaseInsensitive_AndRequiresGguf()
    {
        Assert.Equal(DraftHeadKind.Dflash,
            DraftHeads.DetectSidecar(new[] { "Q4_0.gguf", "DFLASH-model.gguf" }));
        Assert.Equal(DraftHeadKind.Mtp,
            DraftHeads.DetectSidecar(new[] { "MTP-model.GGUF" }));
        // The prefix without the .gguf extension is not a model file.
        Assert.Null(DraftHeads.DetectSidecar(new[] { "model.gguf", "dflash-model.txt" }));
    }

    [Fact]
    public void RepoWithoutSidecars_DetectsNone()
    {
        var files = new[] { "gemma-3-1b-Q4_0.gguf", "mmproj-gemma-3-1b.gguf", "readme.md" };

        Assert.Null(DraftHeads.DetectSidecar(files));
    }

    [Fact]
    public void EmptyListing_DetectsNone()
    {
        Assert.Null(DraftHeads.DetectSidecar([]));
    }

    // ---- spec-type mapping ----

    [Theory]
    [InlineData(DraftHeadKind.Dflash, "draft-dflash")]
    [InlineData(DraftHeadKind.Dspark, "draft-dspark")]
    [InlineData(DraftHeadKind.Mtp, "draft-mtp")]
    public void SpecType_MatchesLlamaCppTokens(DraftHeadKind kind, string expected)
    {
        Assert.Equal(expected, DraftHeads.SpecType(kind));
    }

    // ---- embedded MTP tag ----

    [Theory]
    [InlineData("Qwen3-Next-80B-Q4_K_M-mtp.gguf")]
    [InlineData("model-MTP-Q8_0.gguf")]
    [InlineData("GLM-4.7-Air-Mtp.gguf")]
    public void EmbeddedMtpBuilds_AreRecognized(string fileName)
    {
        Assert.True(DraftHeads.HasEmbeddedMtp(fileName));
    }

    [Theory]
    [InlineData("Qwen3-27B-Q4_0.gguf")]              // no tag
    [InlineData("mtp-model.gguf")]                   // sidecar PREFIX, not an embedded tag
    [InlineData("dflash-model.gguf")]                // other sidecar
    [InlineData("model-mtp")]                        // not a GGUF
    public void PlainFilesAndSidecars_AreNotEmbeddedMtp(string fileName)
    {
        Assert.False(DraftHeads.HasEmbeddedMtp(fileName));
    }

    // ---- repo id parsing ----

    [Theory]
    [InlineData("ggml-org/Qwen3.8-27B-GGUF:Q4_0", "ggml-org/Qwen3.8-27B-GGUF")]
    [InlineData("TheBloke/Mistral-7B-GGUF:Q5_K_M", "TheBloke/Mistral-7B-GGUF")]
    [InlineData("some-org/some-repo", "some-org/some-repo")]   // no quant tag
    [InlineData("local-import-name", "local-import-name")]     // a models-dir id
    public void RepoFromModelId_StripsTheQuantTag(string modelId, string expected)
    {
        Assert.Equal(expected, DraftHeads.RepoFromModelId(modelId));
    }
}
