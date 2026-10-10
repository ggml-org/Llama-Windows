namespace LlamaApp.Llama
{
    /// <summary>
    /// Estimates the total memory a model needs at a given context length:
    /// the on-disk model size (weights, already known) plus the KV cache the
    /// context will occupy. Pure functions over <see cref="GgufContextInfo"/>
    /// so the rules stay unit-testable and the View never derives memory
    /// itself.
    ///
    /// <para>The KV cache holds one K and one V tensor per layer, sized
    /// <c>tokens × kv-width</c>, stored as f16 (2 bytes) by default. Selected
    /// KV types change each tensor's estimated storage. The
    /// kv-width is <c>head_count_kv × key_length</c> for GQA models when those
    /// fields are present, falling back to the full embedding length (a safe
    /// over-estimate for MHA models).</para>
    /// </summary>
    public static class ContextMemoryEstimate
    {
        /// <summary>
        /// The per-token KV cache size in bytes across all layers:
        /// <c>block_count × kv-width × 2 (K+V) × 2 (f16)</c>.
        /// </summary>
        public static long EstimateKvCacheBytes(int contextTokens, GgufContextInfo info,
            string cacheTypeK = "f16", string cacheTypeV = "f16")
        {
            var kvWidth = KvWidth(info);
            var elements = (long)contextTokens * info.BlockCount * kvWidth;
            return TensorBytes(elements, cacheTypeK) + TensorBytes(elements, cacheTypeV);
        }

        private static long TensorBytes(long elements, string type)
        {
            var (blockElements, blockBytes) = type switch
            {
                "f32" => (1, 4),
                "q8_0" => (32, 34),
                "q4_0" or "iq4_nl" => (32, 18),
                "q4_1" => (32, 20),
                "q5_0" => (32, 22),
                "q5_1" => (32, 24),
                _ => (1, 2), // f16, bf16, or unknown: conservative default.
            };
            return ((elements + blockElements - 1) / blockElements) * blockBytes;
        }

        /// <summary>
        /// Estimated total memory: model weights + the context's KV cache.
        /// </summary>
        public static long EstimateTotalBytes(long modelSizeBytes, int contextTokens,
            GgufContextInfo info, string cacheTypeK = "f16", string cacheTypeV = "f16")
            => modelSizeBytes + EstimateKvCacheBytes(contextTokens, info, cacheTypeK, cacheTypeV);

        /// <summary>
        /// The KV width per layer: <c>head_count_kv × key_length</c> when the
        /// attention metadata is present (GQA), derived from head_count when
        /// only the key length is missing, else the embedding length.
        /// </summary>
        private static int KvWidth(GgufContextInfo info)
        {
            var headDim = info.KeyLength
                ?? (info.HeadCount is > 0 ? info.EmbeddingLength / info.HeadCount.Value : 0);
            return info.HeadCountKv is > 0 && headDim > 0
                ? info.HeadCountKv.Value * headDim
                : info.EmbeddingLength;
        }
    }
}
