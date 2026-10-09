using System.Text.Json;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted listen-address setting: it defaults to loopback,
/// survives a JSON round-trip, and a settings.json from before the field
/// shipped deserializes to the same loopback default.
/// </summary>
public sealed class SettingsListenAddressTests
{
    [Fact]
    public void ListenAddress_DefaultsToLocalhost()
    {
        var settings = new Settings();

        Assert.Equal(ListenAddresses.Localhost, settings.ListenAddress);
    }

    [Fact]
    public void ListenAddress_MissingFromSavedJson_DefaultsToLocalhost()
    {
        const string json = """{"ServerPort":9931,"IdleUnloadSeconds":-1}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal(ListenAddresses.Localhost, settings.ListenAddress);
    }

    [Theory]
    [InlineData(ListenAddresses.AllInterfaces)]
    [InlineData(ListenAddresses.Localhost)]
    [InlineData("192.168.1.42")]
    public void ListenAddress_RoundTripsThroughJson(string value)
    {
        var settings = new Settings { ListenAddress = value };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(value, loaded.ListenAddress);
    }
}
