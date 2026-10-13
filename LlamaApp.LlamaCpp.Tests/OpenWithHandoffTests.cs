using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the open-with handoff: a second, unpackaged
/// <c>LlamaApp.exe <file.gguf></c> launch can't forward its command line
/// through the activation redirect (WinUI 3 reports empty launch arguments),
/// so the path travels through a marker file the running instance consumes —
/// exactly once, and only when it holds a loadable .gguf path.
/// </summary>
public sealed class OpenWithHandoffTests : IDisposable
{
    private readonly string _root;

    public OpenWithHandoffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Pending => Path.Combine(_root, ".open-with");

    [Fact]
    public void WriteThenConsume_RoundTrips_Once()
    {
        OpenWithHandoff.Write(@"C:\models\llama-8b.gguf", Pending);

        Assert.Equal(@"C:\models\llama-8b.gguf", OpenWithHandoff.TryConsume(Pending));
        // Consumed: a second take finds nothing.
        Assert.Null(OpenWithHandoff.TryConsume(Pending));
        Assert.False(File.Exists(Pending));
    }

    [Fact]
    public void Write_ReplacesAPreviousPendingRequest()
    {
        OpenWithHandoff.Write(@"C:\a\first.gguf", Pending);
        OpenWithHandoff.Write(@"C:\a\second.gguf", Pending);

        Assert.Equal(@"C:\a\second.gguf", OpenWithHandoff.TryConsume(Pending));
    }

    [Fact]
    public void Consume_Empty_YieldsNull()
    {
        Assert.Null(OpenWithHandoff.TryConsume(Pending));
    }

    [Fact]
    public void Consume_GarbageContent_YieldsNull_AndClearsTheRequest()
    {
        File.WriteAllText(Pending, "not a model path");

        Assert.Null(OpenWithHandoff.TryConsume(Pending));
        Assert.False(File.Exists(Pending)); // consumed exactly once, garbage or not
    }

    [Fact]
    public void Write_CreatesTheDirectory_WhenMissing()
    {
        OpenWithHandoff.Write(@"C:\a\b.gguf", Pending);

        Assert.True(File.Exists(Pending));
    }

    [Fact]
    public void NoTempFile_IsLeftBehind()
    {
        OpenWithHandoff.Write(@"C:\a\b.gguf", Pending);

        Assert.False(File.Exists(Pending + ".tmp"));
    }

    [Fact]
    public void Signal_WakesAListener_AndAutoResets()
    {
        var name = @"Local\Llama.OpenWith.Tests-" + Guid.NewGuid().ToString("N");
        using var listener = OpenWithHandoff.Listen(name);
        Assert.False(listener.WaitOne(0));

        OpenWithHandoff.Signal(name);

        Assert.True(listener.WaitOne(100));
        Assert.False(listener.WaitOne(0)); // auto-reset: consumed by the first wait
    }

    [Fact]
    public void Signal_WithoutListener_IsANoOp()
    {
        var name = @"Local\Llama.OpenWith.Tests-" + Guid.NewGuid().ToString("N");

        OpenWithHandoff.Signal(name); // must not throw — no one is listening
    }
}
