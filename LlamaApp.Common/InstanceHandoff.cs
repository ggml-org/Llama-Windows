namespace LlamaApp.Common;

/// <summary>What a second instance hands to the running one.</summary>
public enum HandoffKind
{
    /// <summary>A model file to import and load (Open With, a shared .gguf).</summary>
    ModelPath,

    /// <summary>Shared text to place in the chat overlay's composer.</summary>
    Text,

    /// <summary>A shared image (.jpg/.jpeg/.png file, or a converted Bitmap
    /// share) to attach in the chat overlay.</summary>
    Image,

    /// <summary>No payload — a plain second launch, which re-opens the flyout.</summary>
    None,
}

/// <summary>
/// A request consumed by the running instance: a model file path, or shared
/// text (exactly one of the two is non-null, per <see cref="HandoffKind"/>).
/// </summary>
public sealed record HandoffRequest(HandoffKind Kind, string? ModelPath, string? Text)
{
    public static readonly HandoffRequest None = new(HandoffKind.None, null, null);

    public static HandoffRequest ForModel(string path) => new(HandoffKind.ModelPath, path, null);

    public static HandoffRequest ForText(string text) => new(HandoffKind.Text, null, text);

    public static HandoffRequest ForImage(string path) => new(HandoffKind.Image, path, null);
}

/// <summary>
/// Hands a request from a second instance to the running one: an "Open With"
/// or shared model file, or text shared through the Windows share sheet.
///
/// <para><b>Why a file and an event:</b> the redirected-activation arguments
/// can't carry everything — WinUI 3 reports empty launch arguments for
/// unpackaged command-line launches (verified), a <c>DataPackageView</c> from
/// a share can't cross a process boundary, and on this setup the
/// <c>AppInstance.Activated</c> callback never fires for redirected instances
/// at all (verified). So the second instance extracts what it needs, writes
/// it to the handoff file and signals a named auto-reset event; the running
/// instance listens on the event and consumes the file. A signal that arrives
/// before the listener exists is harmless — the file remains and is consumed
/// at the end of startup. A plain second launch (no payload) just signals,
/// which re-opens the flyout — restoring the second-launch behavior the
/// broken redirect had been silently losing.</para>
///
/// <para><b>File format:</b> a kind marker line (<c>model</c> / <c>text</c>)
/// followed by the payload — a single path line for models, the remainder of
/// the file for (multi-line) text. A file with no recognized marker is a bare
/// model path — the pre-share format — so an in-flight handoff from an older
/// build still reads correctly.</para>
/// </summary>
public static class InstanceHandoff
{
    /// <summary>The pending-request marker under <see cref="AppData.Root"/> (overridable for tests).</summary>
    public static readonly string PendingPath =
        Path.Combine(AppData.Root, ".open-with");

    /// <summary>Cross-process signal that a request may be waiting. Session-scoped (<c>Local\</c>).</summary>
    public const string EventName = @"Local\Llama.OpenWith";

    // (Event name is parameterized on the helpers for tests — the session-global
    // real name must not be signalled from a test run.)

    /// <summary>Records a pending model-file request, replacing any previous.</summary>
    public static void WriteModel(string modelPath, string? pendingPath = null) =>
        Write(HandoffRequest.ForModel(modelPath), pendingPath ?? PendingPath);

    /// <summary>Records a pending shared-text request, replacing any previous.</summary>
    public static void WriteText(string text, string? pendingPath = null) =>
        Write(HandoffRequest.ForText(text), pendingPath ?? PendingPath);

    /// <summary>Records a pending shared-image request, replacing any previous.</summary>
    public static void WriteImage(string imagePath, string? pendingPath = null) =>
        Write(HandoffRequest.ForImage(imagePath), pendingPath ?? PendingPath);

    /// <summary>Testable core of the Write* methods with an explicit request path.</summary>
    public static void Write(HandoffRequest request, string pendingPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            var payload = request.Kind switch
            {
                HandoffKind.ModelPath => "model\n" + request.ModelPath,
                HandoffKind.Text => "text\n" + request.Text,
                HandoffKind.Image => "image\n" + request.ModelPath,
                _ => null,
            };
            if (payload is null) return;
            var temp = pendingPath + ".tmp";
            File.WriteAllText(temp, payload);
            File.Move(temp, pendingPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "could not record the instance handoff");
        }
    }

    /// <summary>
    /// Takes the pending request, if any — the file is deleted whether or not
    /// it parses. Returns the request when one was waiting, <see cref="HandoffRequest.None"/>
    /// otherwise (and for a plain-second-launch signal with no file at all).
    /// </summary>
    public static HandoffRequest TryConsume(string? pendingPath = null)
    {
        pendingPath ??= PendingPath;
        try
        {
            if (!File.Exists(pendingPath)) return HandoffRequest.None;
            var content = File.ReadAllText(pendingPath);
            File.Delete(pendingPath);
            return Parse(content);
        }
        catch
        {
            return HandoffRequest.None;
        }
    }

    /// <summary>
    /// Parses the handoff payload: <c>model\n&lt;path&gt;</c>,
    /// <c>text\n&lt;multi-line text&gt;</c>, or a bare model path (the
    /// pre-share format). Unrecognized content yields <see cref="HandoffRequest.None"/>.
    /// CRLF line endings are tolerated (the app writes bare LF, but a
    /// hand-edited or externally-written request may carry CR). Pure.
    /// </summary>
    internal static HandoffRequest Parse(string content)
    {
        content = content.Trim();
        if (content.Length == 0) return HandoffRequest.None;

        var markerEnd = content.IndexOf('\n');
        var marker = markerEnd < 0 ? content : content[..markerEnd].TrimEnd('\r');
        var payload = markerEnd < 0 ? "" : content[(markerEnd + 1)..];

        if (string.Equals(marker, "model", StringComparison.Ordinal))
            return payload.Trim().Length > 0
                ? HandoffRequest.ForModel(payload.Trim())
                : HandoffRequest.None;

        if (string.Equals(marker, "text", StringComparison.Ordinal))
            return payload.Length > 0
                ? HandoffRequest.ForText(payload)
                : HandoffRequest.None;

        if (string.Equals(marker, "image", StringComparison.Ordinal))
            return payload.Trim().Length > 0
                ? HandoffRequest.ForImage(payload.Trim())
                : HandoffRequest.None;

        // No marker: the original Open With format — a bare model path.
        return GgufFile.IsGguf(content)
            ? HandoffRequest.ForModel(content)
            : HandoffRequest.None;
    }

    /// <summary>
    /// Signals the running instance that a request needs its attention —
    /// an Open With/share handoff, or a plain second launch. Best-effort:
    /// when no instance is listening the signal is dropped (the handoff file,
    /// if any, is picked up at the end of startup instead).
    /// </summary>
    public static void Signal(string eventName = EventName)
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(eventName);
            existing.Set();
        }
        catch
        {
            // No listener yet — the pending file covers the handoff case.
        }
    }

    /// <summary>
    /// Creates (or opens) the named event a running instance listens on. The
    /// caller owns the returned handle.
    /// </summary>
    public static EventWaitHandle Listen(string eventName = EventName) => new(
        false, EventResetMode.AutoReset, eventName, out _);
}
