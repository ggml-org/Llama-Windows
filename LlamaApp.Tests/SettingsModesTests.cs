using Xunit;

namespace LlamaApp.Tests;

public sealed class SettingsModesTests
{
    [Fact]
    public void UnknownModeRemainsSimpleButSavedAdvancedSettingsAreVisibleToCaller()
    {
        Assert.Equal(SettingsModes.Simple, SettingsModes.Normalize("unknown"));
        Assert.False(SettingsModes.ShowsAdvanced(null));
        Assert.True(SettingsModes.ShowsAdvanced(SettingsModes.Advanced));
        Assert.True(SettingsModes.ShowsExpert(SettingsModes.Expert));

        var settings = new Settings { BatchSize = 512 };
        Assert.True(SettingsModes.HasSavedAdvancedSettings(settings));
        settings.BatchSize = 0;
        Assert.False(SettingsModes.HasSavedAdvancedSettings(settings));
    }
}
