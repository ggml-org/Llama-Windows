using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted <c>Settings.MaxLoadedModels</c> value: safe defaults
/// cap residency at one model, while explicit legacy/opt-in values round-trip.
/// </summary>
public sealed class SettingsModelsMaxTests
{
    [Fact]
    public void MaxLoadedModels_DefaultsToOne()
    {
        var settings = new Settings();

        Assert.Equal(1, settings.MaxLoadedModels);
    }

    [Fact]
    public void MaxLoadedModels_MissingFromSavedJson_DefaultsToOne()
    {
        // A settings.json written before this field shipped has no
        // MaxLoadedModels property. Deserialization must apply the safe
        // default rather than requiring the field.
        const string json = """{"ServerPort":9931,"IdleUnloadSeconds":-1}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal(1, settings.MaxLoadedModels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(64)]
    public void MaxLoadedModels_RoundTripsThroughJson(int value)
    {
        var settings = new Settings { MaxLoadedModels = value };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(value, loaded.MaxLoadedModels);
        Assert.Contains("\"MaxLoadedModels\"", json);
    }
}
