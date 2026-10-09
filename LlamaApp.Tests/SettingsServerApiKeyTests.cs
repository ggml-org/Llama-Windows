using System.Text.Json;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted server API-key setting: empty by default (the
/// loopback bind needs no key), survives a JSON round-trip, and a settings.json
/// from before the field shipped deserializes to the same empty default.
/// </summary>
public sealed class SettingsServerApiKeyTests
{
    [Fact]
    public void ServerApiKey_DefaultsToEmpty()
    {
        var settings = new Settings();

        Assert.Equal("", settings.ServerApiKey);
    }

    [Fact]
    public void ServerApiKey_MissingFromSavedJson_DefaultsToEmpty()
    {
        const string json = """{"ServerPort":9931,"ListenAddress":"127.0.0.1"}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal("", settings.ServerApiKey);
    }

    [Fact]
    public void ServerApiKey_RoundTripsThroughJson()
    {
        var settings = new Settings { ServerApiKey = ServerAuth.GenerateApiKey() };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(settings.ServerApiKey, loaded.ServerApiKey);
    }
}
