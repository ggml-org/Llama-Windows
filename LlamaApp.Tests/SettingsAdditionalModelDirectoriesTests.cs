using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

public sealed class SettingsAdditionalModelDirectoriesTests
{
    [Fact]
    public void MigratesSavedSingleFolderIntoDirectoryList()
    {
        var settings = JsonSerializer.Deserialize<Settings>(
            """{"AdditionalModelsDirectory":"C:\\Models","AdditionalModelDirectories":["D:\\GGUF","c:\\models\\"]}""")!;

        settings.MigrateAdditionalModelDirectories();

        Assert.Equal([@"C:\Models", @"D:\GGUF"], settings.AdditionalModelDirectories);
        Assert.Null(settings.AdditionalModelsDirectory);
        Assert.DoesNotContain("AdditionalModelsDirectory", JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void DirectoryListRoundTripsWithoutLegacySetting()
    {
        var original = new Settings
        {
            AdditionalModelDirectories = [@"C:\Models", @"D:\GGUF"],
        };

        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(original))!;

        Assert.Equal(original.AdditionalModelDirectories, loaded.AdditionalModelDirectories);
        Assert.Null(loaded.AdditionalModelsDirectory);
    }
}
