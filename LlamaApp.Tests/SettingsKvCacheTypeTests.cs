using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted KV cache quantization preferences: both key and
/// value types must default to llama.cpp's own default (<c>f16</c>) and
/// survive a JSON round-trip.
/// </summary>
public sealed class SettingsKvCacheTypeTests
{
    [Fact]
    public void CacheTypes_DefaultToF16()
    {
        var settings = new Settings();

        Assert.Equal("f16", settings.CacheTypeK);
        Assert.Equal("f16", settings.CacheTypeV);
    }

    [Fact]
    public void CacheTypes_MissingFromSavedJson_DefaultToF16()
    {
        // A settings.json written before this field shipped has no CacheTypeK/V
        // properties; deserialization must leave both at f16.
        const string json = """{"ServerPort":9931,"IdleUnloadSeconds":-1}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal("f16", settings.CacheTypeK);
        Assert.Equal("f16", settings.CacheTypeV);
    }

    [Theory]
    [InlineData("f32", "f32")]
    [InlineData("f16", "bf16")]
    [InlineData("q8_0", "q8_0")]
    [InlineData("q4_0", "q5_1")]
    [InlineData("iq4_nl", "q4_1")]
    public void CacheTypes_RoundTripThroughJson(string k, string v)
    {
        var settings = new Settings { CacheTypeK = k, CacheTypeV = v };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(k, loaded.CacheTypeK);
        Assert.Equal(v, loaded.CacheTypeV);
    }
}
