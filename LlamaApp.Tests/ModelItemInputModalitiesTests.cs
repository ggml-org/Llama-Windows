using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

public class ModelItemInputModalitiesTests
{
    [Theory]
    [InlineData("image", true, false, false)]
    [InlineData("audio", false, true, false)]
    [InlineData("video", false, false, true)]
    [InlineData("future-input", false, false, false)]
    [InlineData("AUDIO", false, true, false)]
    public void Declared_Input_Controls_Only_Its_Indicator(
        string modality, bool vision, bool audio, bool video)
    {
        var item = new ModelItem();
        item.ApplyInputModalities(["text", modality]);
        Assert.Equal(vision, item.Vision);
        Assert.Equal(audio, item.Audio);
        Assert.Equal(video, item.Video);
    }

    [Fact]
    public void Existing_Row_Updates_Notifies_And_Clears_All_Indicators()
    {
        var item = new ModelItem { Vision = true };
        var changes = new List<string?>();
        item.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        item.ApplyInputModalities(["text", "audio", "video"]);
        Assert.False(item.Vision);
        Assert.True(item.Audio);
        Assert.True(item.Video);
        Assert.Equal(new[] { "Vision", "Audio", "Video" }, changes);

        changes.Clear();
        item.ApplyInputModalities(["text", "audio", "video"]);
        Assert.Empty(changes);

        item.ApplyInputModalities([]);
        Assert.False(item.Vision);
        Assert.False(item.Audio);
        Assert.False(item.Video);
        Assert.Equal(new[] { "Audio", "Video" }, changes);
    }
}
