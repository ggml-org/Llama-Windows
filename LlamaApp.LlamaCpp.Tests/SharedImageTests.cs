using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the shared-image recognition helpers: the Windows share sheet
/// hands over .jpg/.jpeg/.png files (StorageItems) or raw bitmaps (re-encoded
/// to PNG by the app), and the WebUI paste pipeline needs the right MIME type.
/// </summary>
public sealed class SharedImageTests
{
    [Theory]
    [InlineData("C:\\pics\\shot.png")]
    [InlineData("C:\\pics\\photo.JPG")]
    [InlineData("C:\\pics\\photo.Jpeg")]
    [InlineData("/tmp/img.jpeg")]
    public void IsImagePath_AcceptsShareableImages(string path)
    {
        Assert.True(SharedImage.IsImagePath(path));
    }

    [Theory]
    [InlineData("C:\\models\\llama.gguf")]
    [InlineData("pic.png.exe")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C:\\pics\\shot.webp")]
    public void IsImagePath_RejectsEverythingElse(string? path)
    {
        Assert.False(SharedImage.IsImagePath(path));
    }

    [Theory]
    [InlineData("shot.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.JPEG", "image/jpeg")]
    [InlineData("converted", "image/png")]  // app-written PNGs without extension
    public void MimeTypeFor_MapsTheExtension(string fileName, string expected)
    {
        Assert.Equal(expected, SharedImage.MimeTypeFor(fileName));
    }
}
