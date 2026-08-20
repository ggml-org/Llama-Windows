namespace LlamaApp.Llama
{
    /// <summary>
    /// The context/memory-relevant fields of a GGUF file's metadata header:
    /// the model's architecture, its maximum training context length, and the
    /// dimensions needed to estimate the KV cache (layer count, embedding
    /// length, attention head counts, key length).
    /// </summary>
    /// <param name="Architecture">The <c>general.architecture</c> value, e.g. "llama".</param>
    /// <param name="ContextLength"><c>{arch}.context_length</c> — the model's max context in tokens.</param>
    /// <param name="BlockCount"><c>{arch}.block_count</c> — transformer layer count.</param>
    /// <param name="EmbeddingLength"><c>{arch}.embedding_length</c> — model width.</param>
    /// <param name="HeadCount"><c>{arch}.attention.head_count</c>, when present.</param>
    /// <param name="HeadCountKv"><c>{arch}.attention.head_count_kv</c>, when present (GQA models).</param>
    /// <param name="KeyLength"><c>{arch}.attention.key_length</c> — per-head key dim, when present.</param>
    public sealed record GgufContextInfo(
        string Architecture,
        int ContextLength,
        int BlockCount,
        int EmbeddingLength,
        int? HeadCount,
        int? HeadCountKv,
        int? KeyLength);

    /// <summary>
    /// Reads the context-relevant metadata from a GGUF file's header. Only the
    /// header is touched — the tensor data (the gigabytes) is never read — and
    /// the scan stops as soon as the needed keys are found, so this is fast
    /// even on multi-GB files (the tokenizer arrays that make up most of the
    /// metadata block come after the architecture keys in practice, and a hard
    /// byte bound caps the worst case).
    ///
    /// <para>Kept pure and stream-based so the parsing rules are unit-testable
    /// with in-memory buffers; the file-opening wrapper is a thin shell.</para>
    /// </summary>
    public static class GgufMetadata
    {
        /// <summary>
        /// Safety bound on how far into the file the metadata scan may read.
        /// The needed keys sit at the very start of the metadata block; if they
        /// haven't appeared within this many bytes the file is treated as
        /// unreadable rather than scanning a giant tokenizer array.
        /// </summary>
        public const int MaxHeaderBytes = 16 * 1024 * 1024;

        private const uint GgufMagic = 0x46554747; // "GGUF", little-endian

        /// <summary>
        /// Opens <paramref name="path"/> and parses the context info from its
        /// GGUF header on a thread-pool thread. Returns <c>null</c> on any
        /// failure (missing file, not a GGUF, truncated header) — callers treat
        /// unknown metadata as "no constraints".
        /// </summary>
        public static async Task<GgufContextInfo?> ReadContextInfoAsync(
            string path, CancellationToken cancel = default)
        {
            try
            {
                return await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 1 << 16, FileOptions.SequentialScan);
                    return Parse(fs, cancel);
                }, cancel);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; } // unreadable / not a GGUF — no constraints
        }

        /// <summary>
        /// Parses the GGUF header from <paramref name="stream"/> (positioned at
        /// the file start). Returns <c>null</c> when the stream isn't a GGUF or
        /// the context keys can't be found within <see cref="MaxHeaderBytes"/>.
        /// </summary>
        internal static GgufContextInfo? Parse(Stream stream, CancellationToken cancel = default)
        {
            try
            {
                return ParseCore(stream, cancel);
            }
            catch (EndOfStreamException)
            {
                return null; // truncated header — treat as unreadable
            }
        }

        private static GgufContextInfo? ParseCore(Stream stream, CancellationToken cancel)
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (r.ReadUInt32() != GgufMagic) return null;
            var version = r.ReadUInt32();
            if (version is < 2 or > 3) return null;

            r.ReadUInt64(); // tensor_count — not needed
            var kvCount = r.ReadUInt64();
            if (kvCount > 100_000) return null; // implausible — corrupt header

            string? arch = null;
            int? contextLength = null, blockCount = null, embeddingLength = null;
            int? headCount = null, headCountKv = null, keyLength = null;

            // Values seen before general.architecture was known, keyed by full
            // key name (GGUF files have a single architecture, so suffix
            // matches can't collide). Local to this call — Parse can run on
            // several threads concurrently.
            var early = new Dictionary<string, long>(StringComparer.Ordinal);

            for (ulong i = 0; i < kvCount; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (stream.Position > MaxHeaderBytes) return null;

                var key = ReadString(r);
                if (key is null) return null;

                // Early-exit, checked BEFORE consuming the value: once the
                // required fields are in hand, stop as soon as the key space
                // leaves the architecture block (the tokenizer.* arrays follow
                // it in practice — skipping those would walk megabytes of
                // strings for nothing). general.* keys may be interleaved, so
                // they don't end the block. The optional attention keys are
                // arch-prefixed, so they're always seen before this fires.
                var haveRequired = arch is not null && contextLength is not null &&
                                   blockCount is not null && embeddingLength is not null;
                if (haveRequired &&
                    ((headCount is not null && headCountKv is not null && keyLength is not null) ||
                     (!key.StartsWith(arch + ".", StringComparison.Ordinal) &&
                      !key.StartsWith("general.", StringComparison.Ordinal))))
                    break;

                var type = r.ReadUInt32();

                // Numeric metadata values are uint32 in practice, but GGUF
                // allows several integer widths — read any of them as a long.
                long? ReadNumeric() => TryReadNumeric(r, type);

                switch (key)
                {
                    case "general.architecture":
                        arch = type == 8 ? ReadString(r) : null;
                        if (arch is null) SkipValue(r, type);
                        break;
                    case var k when arch is null && IsWantedKey(k):
                        // Architecture not known yet — remember by suffix; the
                        // arch-prefixed pick happens after the scan. (In
                        // practice general.architecture comes first, so the
                        // arch-specific branch below is the common path.)
                        var v0 = ReadNumeric();
                        if (v0 is null) return null;
                        RememberSuffix(k, v0.Value);
                        break;
                    case var k when arch is not null &&
                                    k.StartsWith(arch + ".", StringComparison.Ordinal):
                        // Only the six wanted keys are read as numbers — every
                        // other arch-prefixed key (rope.freq_base, norm
                        // epsilons, …) is skipped by type. Reading them as
                        // numeric would abort the parse: those are FLOAT32/
                        // FLOAT64/ARRAY values on every real model.
                        if (k[(arch.Length + 1)..] is { } suffix && IsWantedSuffix(suffix))
                        {
                            var v = ReadNumeric();
                            if (v is null) return null; // wanted key, unexpected type — corrupt
                            switch (suffix)
                            {
                                case "context_length": contextLength = v.Value > 0 ? (int)v.Value : null; break;
                                case "block_count": blockCount = (int)v.Value; break;
                                case "embedding_length": embeddingLength = (int)v.Value; break;
                                case "attention.head_count": headCount = (int)v.Value; break;
                                case "attention.head_count_kv": headCountKv = (int)v.Value; break;
                                case "attention.key_length": keyLength = (int)v.Value; break;
                            }
                        }
                        else
                        {
                            SkipValue(r, type);
                        }
                        break;
                    default:
                        SkipValue(r, type);
                        break;
                }

            }

            // Resolve any suffix-remembered values that arrived before the
            // architecture key (unusual ordering).
            if (arch is not null)
            {
                contextLength ??= TakeSuffix(arch + ".context_length");
                blockCount ??= TakeSuffix(arch + ".block_count");
                embeddingLength ??= TakeSuffix(arch + ".embedding_length");
                headCount ??= TakeSuffix(arch + ".attention.head_count");
                headCountKv ??= TakeSuffix(arch + ".attention.head_count_kv");
                keyLength ??= TakeSuffix(arch + ".attention.key_length");
            }

            return arch is not null && contextLength is > 0 &&
                   blockCount is > 0 && embeddingLength is > 0
                ? new GgufContextInfo(arch, contextLength.Value, blockCount.Value,
                    embeddingLength.Value, headCount, headCountKv, keyLength)
                : null;

            static bool IsWantedKey(string k) =>
                k.EndsWith(".context_length", StringComparison.Ordinal) ||
                k.EndsWith(".block_count", StringComparison.Ordinal) ||
                k.EndsWith(".embedding_length", StringComparison.Ordinal) ||
                k.EndsWith(".attention.head_count", StringComparison.Ordinal) ||
                k.EndsWith(".attention.head_count_kv", StringComparison.Ordinal) ||
                k.EndsWith(".attention.key_length", StringComparison.Ordinal);

            static bool IsWantedSuffix(string s) =>
                s is "context_length" or "block_count" or "embedding_length" or
                     "attention.head_count" or "attention.head_count_kv" or "attention.key_length";

            void RememberSuffix(string key, long value) => early[key] = value;
            int? TakeSuffix(string key) =>
                early.Remove(key, out var v) ? (int)v : null;
        }

        /// <summary>Reads a GGUF string (uint64 length + UTF-8 bytes); null when truncated.</summary>
        private static string? ReadString(BinaryReader r)
        {
            var len = r.ReadUInt64();
            if (len > MaxHeaderBytes) return null; // implausible — corrupt
            var bytes = r.ReadBytes((int)len);
            return bytes.Length == (int)len
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : null;
        }

        /// <summary>Reads any GGUF scalar integer type as a long; null for non-numeric types.</summary>
        private static long? TryReadNumeric(BinaryReader r, uint type) => type switch
        {
            0 => r.ReadByte(),                       // UINT8
            1 => r.ReadSByte(),                      // INT8
            2 => r.ReadUInt16(),                     // UINT16
            3 => r.ReadInt16(),                      // INT16
            4 => r.ReadUInt32(),                     // UINT32
            5 => r.ReadInt32(),                      // INT32
            7 => r.ReadByte(),                       // BOOL
            10 => (long)r.ReadUInt64(),              // UINT64
            11 => r.ReadInt64(),                     // INT64
            _ => null,
        };

        /// <summary>Skips a metadata value of the given GGUF type.</summary>
        private static void SkipValue(BinaryReader r, uint type)
        {
            switch (type)
            {
                case 0 or 1 or 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;
                case 2 or 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;
                case 4 or 5 or 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;
                case 8: ReadString(r); break;
                case 10 or 11 or 12: r.BaseStream.Seek(8, SeekOrigin.Current); break;
                case 9: // ARRAY: element type + count + elements
                    var elemType = r.ReadUInt32();
                    var count = r.ReadUInt64();
                    if (elemType == 8)
                    {
                        for (ulong i = 0; i < count; i++) ReadString(r);
                    }
                    else
                    {
                        var elemSize = elemType switch
                        {
                            0 or 1 or 7 => 1L,
                            2 or 3 => 2L,
                            4 or 5 or 6 => 4L,
                            10 or 11 or 12 => 8L,
                            _ => 0L,
                        };
                        r.BaseStream.Seek((long)(elemSize * (long)count), SeekOrigin.Current);
                    }
                    break;
            }
        }
    }
}
