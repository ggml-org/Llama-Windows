using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted free-form "custom serve arguments" setting: it
/// defaults to empty (no extra flags) and survives a JSON round-trip exactly
/// as typed, quotes and newlines included.
/// </summary>
public sealed class SettingsCustomServeArgumentsTests
{
    [Fact]
    public void CustomServeArguments_DefaultsToEmpty()
    {
        var settings = new Settings();

        Assert.Equal("", settings.CustomServeArguments);
    }

    [Fact]
    public void CustomServeArguments_MissingFromSavedJson_DefaultsToEmpty()
    {
        // A settings.json written before this field shipped has no
        // CustomServeArguments property; deserialization must leave it empty.
        const string json = """{"ServerPort":9931,"IdleUnloadSeconds":-1}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal("", settings.CustomServeArguments);
    }

    [Theory]
    [InlineData("--threads 4 --flash-attn")]
    [InlineData("--alias \"My Fine Model\"\r\n--verbose")]
    [InlineData("--foo=bar")]
    public void CustomServeArguments_RoundTripsThroughJson(string value)
    {
        var settings = new Settings { CustomServeArguments = value };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(value, loaded.CustomServeArguments);
    }
}
