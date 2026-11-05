using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Regression tests for the idle-unload default and its migration semantics.
/// Missing settings inherit the safe default; an explicit user choice still
/// wins, including -1 (never unload).
/// </summary>
public sealed class SettingsIdleUnloadTests
{
    [Fact]
    public void IdleUnload_DefaultsToFiveMinutes()
    {
        var settings = new Settings();

        Assert.Equal(300, settings.IdleUnloadSeconds);
    }

    [Fact]
    public void IdleUnload_MissingFromSavedJson_DefaultsToFiveMinutes()
    {
        const string json = """{"ServerPort":9931,"MaxLoadedModels":1}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal(300, settings.IdleUnloadSeconds);
    }

    [Fact]
    public void ProductionDeserializer_AppliesBothSafeDefaults_WhenFieldsAreMissing()
    {
        const string json = """{"ServerPort":9931}""";

        var settings = JsonSerializer.Deserialize(
            json, SettingsJsonContext.Default.Settings);

        Assert.NotNull(settings);
        Assert.Equal(300, settings.IdleUnloadSeconds);
        Assert.Equal(1, settings.MaxLoadedModels);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(300)]
    [InlineData(900)]
    [InlineData(3600)]
    public void IdleUnload_RoundTripsThroughJson(int value)
    {
        var settings = new Settings { IdleUnloadSeconds = value };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(value, loaded.IdleUnloadSeconds);
        Assert.Contains("\"IdleUnloadSeconds\"", json);
    }

    [Fact]
    public void ExplicitLegacyOptOuts_AreNotRewrittenByDeserialization()
    {
        const string json = """{"IdleUnloadSeconds":-1,"MaxLoadedModels":0}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal(-1, settings.IdleUnloadSeconds);
        Assert.Equal(0, settings.MaxLoadedModels);
    }
}
