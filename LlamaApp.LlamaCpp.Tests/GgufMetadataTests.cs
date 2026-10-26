using System.Diagnostics;
using System.Text;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the GGUF header reader (<see cref="GgufMetadata.Parse"/>).
/// The details view's context selector is only as trustworthy as this parse:
/// a misread context_length would mark options supported that the model can't
/// serve, and a misread KV geometry would skew every memory estimate. The
/// buffers are hand-built minimal headers — no multi-GB fixtures needed,
/// since the parser only ever touches the header.
/// </summary>
public sealed class GgufMetadataTests
{
    // ---- Minimal GGUF header builder ----

    private static byte[] Gguf(params Action<BinaryWriter>[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(0x46554747u);           // magic "GGUF"
        w.Write(3u);                    // version
        w.Write(0ul);                   // tensor_count
        w.Write((ulong)entries.Length); // metadata_kv_count
        foreach (var e in entries) e(w);
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteKey(BinaryWriter w, string key)
    {
        var b = Encoding.UTF8.GetBytes(key);
        w.Write((ulong)b.Length);
        w.Write(b);
    }

    private static Action<BinaryWriter> Str(string key, string value) => w =>
    {
        WriteKey(w, key);
        w.Write(8u); // STRING
        var b = Encoding.UTF8.GetBytes(value);
        w.Write((ulong)b.Length);
        w.Write(b);
    };

    private static Action<BinaryWriter> U32(string key, uint value) => w =>
    {
        WriteKey(w, key);
        w.Write(4u); // UINT32
        w.Write(value);
    };

    private static Action<BinaryWriter> U64(string key, ulong value) => w =>
    {
        WriteKey(w, key);
        w.Write(10u); // UINT64
        w.Write(value);
    };

    private static Action<BinaryWriter> F32(string key, float value) => w =>
    {
        WriteKey(w, key);
        w.Write(6u); // FLOAT32
        w.Write(value);
    };

    private static Action<BinaryWriter> StrArray(string key, params string[] values) => w =>
    {
        WriteKey(w, key);
        w.Write(9u);  // ARRAY
        w.Write(8u);  // of STRING
        w.Write((ulong)values.Length);
        foreach (var v in values)
        {
            var b = Encoding.UTF8.GetBytes(v);
            w.Write((ulong)b.Length);
            w.Write(b);
        }
    };

    private static Action<BinaryWriter> RawArray(string key, uint elemType, ulong count) => w =>
    {
        WriteKey(w, key);
        w.Write(9u);  // ARRAY
        w.Write(elemType);
        w.Write(count);
    };

    private static GgufContextInfo? Parse(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return GgufMetadata.Parse(ms);
    }

    /// <summary>The standard llama-shaped header, in the usual key order.</summary>
    private static Action<BinaryWriter>[] LlamaHeader(uint contextLength = 131072) =>
    [
        Str("general.architecture", "llama"),
        U32("llama.context_length", contextLength),
        U32("llama.block_count", 32),
        U32("llama.embedding_length", 4096),
        U32("llama.attention.head_count", 32),
        U32("llama.attention.head_count_kv", 8),
        U32("llama.attention.key_length", 128),
    ];

    [Fact]
    public void Parse_Reads_The_Context_Info_From_A_Minimal_Header()
    {
        var info = Parse(Gguf(LlamaHeader()));

        Assert.NotNull(info);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(131072, info.ContextLength);
        Assert.Equal(32, info.BlockCount);
        Assert.Equal(4096, info.EmbeddingLength);
        Assert.Equal(8, info.HeadCountKv);
        Assert.Equal(32, info.HeadCount);
        Assert.Equal(128, info.KeyLength);
    }

    [Fact]
    public void Parse_Stops_Before_The_Tokenizer_Arrays()
    {
        // Real GGUFs carry the (huge) tokenizer arrays after the architecture
        // keys; the scan must stop as soon as it has what it needs rather than
        // walking megabytes of tokens.
        var bytes = Gguf([.. LlamaHeader(),
            StrArray("tokenizer.ggml.tokens", "a", "b", "c", "d")]);
        using var ms = new MemoryStream(bytes);

        var info = GgufMetadata.Parse(ms);

        Assert.NotNull(info);
        Assert.True(ms.Position < ms.Length,
            "the parser read past the keys it needed — the early-exit is broken");
    }

    [Fact]
    public void Parse_Skips_Unknown_Keys_Of_Every_Value_Shape()
    {
        // Unknown keys (strings, floats, wide ints, arrays) before the wanted
        // ones must be skipped by type, not by assumed width.
        var info = Parse(Gguf(
        [
            Str("general.name", "Some Model"),
            U64("general.size_label", 123),
            F32("llama.attention.layer_norm_rms_epsilon", 1e-5f),
            StrArray("tokenizer.ggml.merges", "a b", "c d"),
            .. LlamaHeader(),
        ]));

        Assert.NotNull(info);
        Assert.Equal(131072, info.ContextLength);
    }

    [Fact]
    public void Parse_Skips_Float_And_Array_Keys_Inside_The_Architecture_Block()
    {
        // Real-model key order (gemma3): FLOAT32 arch-prefixed keys are
        // interleaved with the wanted ones (layer_norm_rms_epsilon,
        // rope.freq_base). Reading those as numeric used to abort the whole
        // parse; they must be skipped by type.
        var info = Parse(Gguf(
        [
            Str("general.architecture", "gemma3"),
            StrArray("general.tags", "text-generation"),
            U32("gemma3.context_length", 131072),
            U32("gemma3.embedding_length", 2048),
            U32("gemma3.block_count", 26),
            U32("gemma3.feed_forward_length", 16384),
            U32("gemma3.attention.head_count", 8),
            F32("gemma3.attention.layer_norm_rms_epsilon", 1e-6f),
            U32("gemma3.attention.key_length", 256),
            F32("gemma3.rope.freq_base", 1000000f),
            U32("gemma3.attention.head_count_kv", 4),
            StrArray("tokenizer.ggml.tokens", "a", "b"),
        ]));

        Assert.NotNull(info);
        Assert.Equal("gemma3", info.Architecture);
        Assert.Equal(131072, info.ContextLength);
        Assert.Equal(26, info.BlockCount);
        Assert.Equal(2048, info.EmbeddingLength);
        Assert.Equal(8, info.HeadCount);
        Assert.Equal(4, info.HeadCountKv);
        Assert.Equal(256, info.KeyLength);
    }

    [Fact]
    public void Parse_Tolerates_Context_Keys_Before_The_Architecture_Key()
    {
        // general.architecture practically always comes first, but GGUF does
        // not order keys — the suffix-remember path must still resolve them.
        var info = Parse(Gguf(
            U32("llama.context_length", 8192),
            U32("llama.block_count", 24),
            U32("llama.embedding_length", 2048),
            Str("general.architecture", "llama")));

        Assert.NotNull(info);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(8192, info.ContextLength);
        Assert.Equal(24, info.BlockCount);
        Assert.Equal(2048, info.EmbeddingLength);
    }

    [Fact]
    public void Parse_Reads_Numeric_Values_Of_Any_Integer_Width()
    {
        // GGUF allows several integer widths for the same logical value.
        var info = Parse(Gguf(
            Str("general.architecture", "llama"),
            U64("llama.context_length", 65536),
            U32("llama.block_count", 32),
            U32("llama.embedding_length", 4096)));

        Assert.NotNull(info);
        Assert.Equal(65536, info.ContextLength);
    }

    [Fact]
    public void Parse_Bounds_The_Array_Skip_For_An_Enormous_Count()
    {
        // Parse-level smoke: a corrupt/hostile header claiming an ARRAY of
        // ~2^64 elements bails to null instead of seeking (or looping) on
        // that count. The mechanism is pinned by the SkipValue_* tests below.
        var sw = Stopwatch.StartNew();

        Assert.Null(Parse(Gguf(
        [
            RawArray("some.ints", 4u /* UINT32 */, ulong.MaxValue / 2),
            .. LlamaHeader(),
        ])));
        Assert.Null(Parse(Gguf(
        [
            RawArray("some.strings", 8u /* STRING */, ulong.MaxValue / 2),
            .. LlamaHeader(),
        ])));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"an enormous ARRAY count must be bounded, took {sw.Elapsed}");
    }

    // ---- SkipValue directly: the array skip's alignment/budget contract ----

    /// <summary>Runs <see cref="GgufMetadata.SkipValue"/> over a crafted
    /// value payload and reports success plus the end position.</summary>
    private static (bool Ok, long Position) Skip(byte[] value, uint type)
    {
        using var ms = new MemoryStream(value);
        using var r = new BinaryReader(ms);
        var ok = GgufMetadata.SkipValue(r, type, CancellationToken.None);
        return (ok, ms.Position);
    }

    /// <summary>Raw ARRAY value payload: elemType + count (+ optional body).
    /// No key prefix — SkipValue reads the value only.</summary>
    private static byte[] ArrayValue(uint elemType, ulong count, byte[]? body = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(elemType);
        w.Write(count);
        if (body is not null) w.Write(body);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void SkipValue_Returns_False_For_An_Enormous_Array_Count_Without_Seeking()
    {
        // Both array flavors must bail after reading just elemType + count —
        // seeking count*elemSize (or looping count strings) on a ~2^64 count
        // is exactly the misalignment/hostility the skip must refuse.
        foreach (var elemType in new uint[] { 4u /* UINT32 */, 8u /* STRING */ })
        {
            var (ok, pos) = Skip(ArrayValue(elemType, ulong.MaxValue / 2), 9u /* ARRAY */);

            Assert.False(ok);
            Assert.True(pos <= GgufMetadata.MaxHeaderBytes,
                $"elemType {elemType}: skipped to {pos}, far past the budget");
        }
    }

    [Fact]
    public void SkipValue_Returns_False_For_An_Unknown_Outer_Type()
    {
        // A GGUF type >12 has an unknowable width: skipping nothing and
        // reporting success would misalign every following KV. Must fail.
        var (ok, pos) = Skip([0xAB, 0xCD, 0xEF], 42u);

        Assert.False(ok);
        Assert.Equal(0, pos); // fails fast — nothing consumed
    }

    [Fact]
    public void SkipValue_Returns_False_For_An_Unknown_Array_Element_Type()
    {
        // elemSize == 0 (unknown elem type, incl. nested ARRAY=9): seeking
        // count*0 = 0 bytes and returning true would silently misalign.
        foreach (var elemType in new uint[] { 9u /* nested ARRAY */, 42u /* unknown */ })
        {
            var (ok, pos) = Skip(ArrayValue(elemType, count: 1), 9u /* ARRAY */);

            Assert.False(ok);
            Assert.True(pos <= GgufMetadata.MaxHeaderBytes,
                $"elemType {elemType}: skipped to {pos}, far past the budget");
        }
    }

    [Fact]
    public void SkipValue_Returns_False_When_A_Truncated_Value_Would_Seek_Past_The_End()
    {
        // A numeric ARRAY whose element body is cut short: count*elemSize is
        // within the header budget but the bytes are not actually present.
        // Skipping past EOF on a truncated FINAL value is a corrupt header,
        // not something to "skip cleanly".
        var (arrayOk, _) = Skip(
            ArrayValue(4u /* UINT32 */, 4, body: new byte[4]), 9u /* ARRAY */);
        Assert.False(arrayOk);

        // And a scalar UINT32 with only 2 bytes behind it.
        var (scalarOk, _) = Skip(new byte[2], 4u /* UINT32 */);
        Assert.False(scalarOk);
    }

    [Fact]
    public void SkipValue_Checks_The_Budget_Every_String_Array_Step()
    {
        // 1-byte strings: each consumes 9 bytes (uint64 length + 1 byte).
        // Enough of them to cross the header budget mid-walk — the walk must
        // stop at the FIRST string past the budget, not up to 1024 strings
        // later (the old stride-1024 check read a whole window past it).
        const int BytesPerString = 9;
        var count = (ulong)(GgufMetadata.MaxHeaderBytes / BytesPerString) + 4096;
        var body = new byte[checked((int)count * BytesPerString)];
        for (int i = 0; i < (int)count; i++)
        {
            body[i * BytesPerString] = 1;        // uint64 length = 1 (LE low byte)
            body[i * BytesPerString + 8] = 0x2A; // the single content byte
        }

        var (ok, pos) = Skip(ArrayValue(8u /* STRING */, count, body), 9u /* ARRAY */);

        Assert.False(ok);
        // First failing step sits at most one string past the budget; the old
        // stride-1024 check could land ~1024 strings (9 KB) past it.
        Assert.True(pos <= GgufMetadata.MaxHeaderBytes + BytesPerString,
            $"string-array walk ran to {pos}, past budget+1 step ({GgufMetadata.MaxHeaderBytes + BytesPerString})");
    }

    [Fact]
    public void Parse_Returns_Null_For_A_Non_Gguf_Stream()
    {
        var info = Parse(Encoding.UTF8.GetBytes("this is not a gguf file at all"));
        Assert.Null(info);
    }

    [Fact]
    public void Parse_Returns_Null_For_A_Truncated_Header()
    {
        var full = Gguf(LlamaHeader());
        var truncated = full[..(full.Length / 2)];

        Assert.Null(Parse(truncated));
    }

    [Fact]
    public void Parse_Returns_Null_When_The_Context_Length_Is_Missing()
    {
        // Without a max context the selector can't constrain anything —
        // "unknown" must surface as null, not as a made-up number.
        var info = Parse(Gguf(
            Str("general.architecture", "llama"),
            U32("llama.block_count", 32),
            U32("llama.embedding_length", 4096)));

        Assert.Null(info);
    }

    [Fact]
    public async Task ReadContextInfoAsync_Returns_Null_For_A_Missing_File()
    {
        var info = await GgufMetadata.ReadContextInfoAsync(
            Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".gguf"));

        Assert.Null(info);
    }

    [Fact]
    public async Task ReadContextInfoAsync_Reads_A_Real_File_On_Disk()
    {
        var path = Path.Combine(Path.GetTempPath(), "gguf-test-" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            await File.WriteAllBytesAsync(path, Gguf(LlamaHeader(32768)));

            var info = await GgufMetadata.ReadContextInfoAsync(path);

            Assert.NotNull(info);
            Assert.Equal(32768, info.ContextLength);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
