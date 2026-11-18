using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted <c>Settings.MaxLoadedModels</c> value: the default
/// of 0 bounds model residency by memory (with a 1024 MiB margin) rather than
/// by count, while explicit values — including positive count caps —
/// round-trip.
/// </summary>
public sealed class SettingsModelsMaxTests
{
    [Fact]
    public void MaxLoadedModels_DefaultsToZero()
    {
        var settings = new Settings();

        Assert.Equal(0, settings.MaxLoadedModels);
    }

    [Fact]
    public void MaxLoadedModels_MissingFromSavedJson_DefaultsToZero()
    {
        // A settings.json written before this field shipped has no
        // MaxLoadedModels property. Deserialization must apply the safe
        // default (0 = memory-bounded residency) rather than requiring the
        // field.
        const string json = """{"ServerPort":9931,"IdleUnloadSeconds":-1}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal(0, settings.MaxLoadedModels);
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
