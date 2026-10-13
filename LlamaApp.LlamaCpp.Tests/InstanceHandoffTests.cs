using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the second-instance handoff: model paths ("Open With", a shared
/// .gguf) and text (a Windows share) travel through a marker-prefixed payload
/// file plus a named event, because the redirected-activation arguments can't
/// carry them (empty for unpackaged launches; a share's DataPackageView can't
/// cross a process boundary). A plain second launch just signals, which
/// re-opens the flyout.
/// </summary>
public sealed class InstanceHandoffTests : IDisposable
{
    private readonly string _root;

    public InstanceHandoffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Pending => Path.Combine(_root, ".open-with");

    // ---- Model requests (Open With / shared .gguf) ----

    [Fact]
    public void WriteModelThenConsume_RoundTrips_Once()
    {
        InstanceHandoff.WriteModel(@"C:\models\llama-8b.gguf", Pending);

        var first = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.ModelPath, first.Kind);
        Assert.Equal(@"C:\models\llama-8b.gguf", first.ModelPath);
        Assert.Null(first.Text);

        // Consumed: a second take finds nothing.
        Assert.Equal(HandoffKind.None, InstanceHandoff.TryConsume(Pending).Kind);
        Assert.False(File.Exists(Pending));
    }

    [Fact]
    public void Write_ReplacesAPreviousPendingRequest()
    {
        InstanceHandoff.Write(HandoffRequest.ForModel(@"C:\a\first.gguf"), Pending);
        InstanceHandoff.Write(HandoffRequest.ForModel(@"C:\a\second.gguf"), Pending);

        var taken = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(@"C:\a\second.gguf", taken.ModelPath);
    }

    // ---- Text requests (Windows share sheet) ----

    [Fact]
    public void WriteTextThenConsume_RoundTrips_MultiLine()
    {
        var text = "Summarize this:\r\n\r\nSecond line \"quoted\" — and more...\r\nlast";
        InstanceHandoff.WriteText(text, Pending);

        var taken = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.Text, taken.Kind);
        Assert.Null(taken.ModelPath);
        Assert.Equal(text, taken.Text);
    }

    [Fact]
    public void WriteImageThenConsume_RoundTrips()
    {
        InstanceHandoff.WriteImage(@"C:\Users\me\Pictures\shot.png", Pending);

        var taken = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.Image, taken.Kind);
        Assert.Equal(@"C:\Users\me\Pictures\shot.png", taken.ModelPath);
        Assert.Null(taken.Text);

        Assert.Equal(HandoffKind.None, InstanceHandoff.TryConsume(Pending).Kind);
        Assert.False(File.Exists(Pending));
    }

    [Fact]
    public void ImageRequest_ReplacesModelAndText()
    {
        InstanceHandoff.WriteModel(@"C:\a\model.gguf", Pending);
        InstanceHandoff.WriteImage(@"C:\p\img.jpg", Pending);

        var taken = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.Image, taken.Kind);

        InstanceHandoff.WriteText("prompt", Pending);
        var back = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.Text, back.Kind);
    }

    [Fact]
    public void TextRequest_ReplacesAModelRequest_AndBack()
    {
        InstanceHandoff.WriteModel(@"C:\a\model.gguf", Pending);
        InstanceHandoff.WriteText("replacing with a prompt", Pending);

        var first = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.Text, first.Kind);

        InstanceHandoff.WriteText("prompt two", Pending);
        var second = InstanceHandoff.TryConsume(Pending);
        Assert.Equal(HandoffKind.Text, second.Kind);
        Assert.Equal("prompt two", second.Text);
    }

    // ---- Parse: format handling ----

    [Fact]
    public void Parse_CRLFLineEndings_ReadLikeLF()
    {
        // The app writes bare LF, but a hand-edited or externally-written
        // request may carry CRLF — the marker must not end up "text\r".
        var request = InstanceHandoff.Parse("text\r\nSummarize this.\r\nSecond line.");

        Assert.Equal(HandoffKind.Text, request.Kind);
        Assert.Equal("Summarize this.\r\nSecond line.", request.Text);

        var model = InstanceHandoff.Parse("model\r\nC:\\models\\m.gguf");
        Assert.Equal(HandoffKind.ModelPath, model.Kind);
        Assert.Equal(@"C:\models\m.gguf", model.ModelPath);
    }

    [Fact]
    public void Parse_BareModelPath_ThePreShareFormat_StillReads()
    {
        var request = InstanceHandoff.Parse(@"C:\models\old-format.gguf");

        Assert.Equal(HandoffKind.ModelPath, request.Kind);
        Assert.Equal(@"C:\models\old-format.gguf", request.ModelPath);
    }

    [Theory]
    [InlineData("not-a-model-path")]
    [InlineData("text\n")]
    [InlineData("model\n")]
    [InlineData("")]
    public void Parse_UnusableContent_YieldsNone(string content)
    {
        Assert.Equal(HandoffKind.None, InstanceHandoff.Parse(content).Kind);
    }

    [Fact]
    public void TryConsume_GarbageContent_YieldsNone_AndClearsTheRequest()
    {
        File.WriteAllText(Pending, "not a request");

        Assert.Equal(HandoffKind.None, InstanceHandoff.TryConsume(Pending).Kind);
        Assert.False(File.Exists(Pending)); // consumed exactly once, garbage or not
    }

    [Fact]
    public void Write_CreatesTheDirectory_WhenMissing()
    {
        InstanceHandoff.Write(HandoffRequest.ForModel(@"C:\a\b.gguf"), Pending);

        Assert.True(File.Exists(Pending));
    }

    [Fact]
    public void NoTempFile_IsLeftBehind()
    {
        InstanceHandoff.Write(HandoffRequest.ForModel(@"C:\a\b.gguf"), Pending);

        Assert.False(File.Exists(Pending + ".tmp"));
    }

    // ---- Signal / listen ----

    [Fact]
    public void Signal_WakesAListener_AndAutoResets()
    {
        var name = @"Local\Llama.OpenWith.Tests-" + Guid.NewGuid().ToString("N");
        using var listener = InstanceHandoff.Listen(name);
        Assert.False(listener.WaitOne(0));

        InstanceHandoff.Signal(name);

        Assert.True(listener.WaitOne(100));
        Assert.False(listener.WaitOne(0)); // auto-reset: consumed by the first wait
    }

    [Fact]
    public void Signal_WithoutListener_IsANoOp()
    {
        var name = @"Local\Llama.OpenWith.Tests-" + Guid.NewGuid().ToString("N");

        InstanceHandoff.Signal(name); // must not throw — no one is listening
    }
}
