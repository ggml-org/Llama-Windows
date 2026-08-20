using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the context-length memory estimate
/// (<see cref="ContextMemoryEstimate"/>). The number shown next to every
/// context option drives a real decision (can this machine hold that
/// context?), so the KV-cache math — especially the GQA narrowing — is pinned
/// down exactly.
/// </summary>
public sealed class ContextMemoryEstimateTests
{
    /// <summary>Llama-3-8B-shaped metadata: 32 layers, 4096-wide, GQA 8 KV heads × 128 dim.</summary>
    private static readonly GgufContextInfo Llama8B = new(
        Architecture: "llama",
        ContextLength: 131072,
        BlockCount: 32,
        EmbeddingLength: 4096,
        HeadCount: 32,
        HeadCountKv: 8,
        KeyLength: 128);

    [Fact]
    public void KvCache_Uses_The_Gqa_Kv_Width_When_Attention_Metadata_Is_Present()
    {
        // kv-width = head_count_kv × key_length = 8 × 128 = 1024 (not the full
        // 4096 embedding width — GQA is exactly why the estimate must read the
        // attention fields).
        // 4096 tokens × 32 layers × 1024 × 2 (K+V) × 2 (f16) = 512 MiB.
        Assert.Equal(512L * 1024 * 1024,
            ContextMemoryEstimate.EstimateKvCacheBytes(4096, Llama8B));
    }

    [Fact]
    public void KvCache_Scales_Linearly_With_The_Context_Length()
    {
        var at4k = ContextMemoryEstimate.EstimateKvCacheBytes(4096, Llama8B);
        var at32k = ContextMemoryEstimate.EstimateKvCacheBytes(32768, Llama8B);

        Assert.Equal(at4k * 8, at32k);
    }

    [Fact]
    public void KvCache_Falls_Back_To_The_Embedding_Width_Without_Attention_Metadata()
    {
        // MHA models often omit head_count_kv; the embedding length is the
        // safe over-estimate (kv-width == embedding width for MHA).
        var mha = Llama8B with { HeadCount = null, HeadCountKv = null, KeyLength = null };

        // 4096 tokens × 32 layers × 4096 × 2 × 2 = 2 GiB.
        Assert.Equal(2L * 1024 * 1024 * 1024,
            ContextMemoryEstimate.EstimateKvCacheBytes(4096, mha));
    }

    [Fact]
    public void KvCache_Derives_The_Head_Dim_From_The_Head_Count_When_Key_Length_Is_Missing()
    {
        // head_dim = embedding_length / head_count = 4096 / 32 = 128;
        // kv-width = 8 × 128 = 1024 — same as the explicit key_length case.
        var noKeyLength = Llama8B with { KeyLength = null };

        Assert.Equal(
            ContextMemoryEstimate.EstimateKvCacheBytes(4096, Llama8B),
            ContextMemoryEstimate.EstimateKvCacheBytes(4096, noKeyLength));
    }

    [Fact]
    public void Total_Adds_The_Model_Size_To_The_Kv_Cache()
    {
        const long modelSize = 4_000_000_000L;

        var total = ContextMemoryEstimate.EstimateTotalBytes(modelSize, 4096, Llama8B);

        Assert.Equal(modelSize + 512L * 1024 * 1024, total);
    }
}
