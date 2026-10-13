namespace LlamaApp.Common;

/// <summary>
/// Hands an "Open With" model file from a second instance to the running one.
///
/// <para><b>Why a file and an event:</b> MSIX file activations carry their path
/// through the redirected activation arguments, but on this setup the
/// <c>AppInstance.Activated</c> callback never fires for redirected instances
/// (verified — unpackaged and the app's packaged dev registration alike), so
/// arguments alone can't reach the running instance. The second instance
/// writes the path to the handoff file and signals a named auto-reset event;
/// the running instance listens on the event and consumes the file. A signal
/// that arrives before the listener exists is harmless — the file remains and
/// is consumed at the end of startup. A plain second launch (no model) writes
/// nothing and just signals, which re-opens the flyout — restoring the
/// second-launch behavior the broken redirect had been silently losing.</para>
/// </summary>
public static class OpenWithHandoff
{
    /// <summary>The pending-request marker under <see cref="AppData.Root"/> (overridable for tests).</summary>
    public static readonly string PendingPath =
        Path.Combine(AppData.Root, ".open-with");

    /// <summary>Cross-process signal that a request may be waiting. Session-scoped (<c>Local\</c>).</summary>
    public const string EventName = @"Local\Llama.OpenWith";

    // (Event name is parameterized on the helpers for tests — the session-global
    // real name must not be signalled from a test run.)

    /// <summary>Records one pending open request, replacing any previous.</summary>
    public static void Write(string modelPath) => Write(modelPath, PendingPath);

    /// <summary>Testable core of <see cref="Write(string)"/> with an explicit request path.</summary>
    public static void Write(string modelPath, string pendingPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            var temp = pendingPath + ".tmp";
            File.WriteAllText(temp, modelPath);
            File.Move(temp, pendingPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "could not record the open-with handoff");
        }
    }

    /// <summary>
    /// Takes the pending request, if any — the file is deleted whether or not
    /// it holds a loadable .gguf path. Returns the path when one was waiting,
    /// null otherwise.
    /// </summary>
    public static string? TryConsume() => TryConsume(PendingPath);

    /// <summary>Testable core of <see cref="TryConsume()"/> with an explicit request path.</summary>
    public static string? TryConsume(string pendingPath)
    {
        try
        {
            if (!File.Exists(pendingPath)) return null;
            var path = File.ReadAllText(pendingPath).Trim();
            File.Delete(pendingPath);
            return GgufFile.IsGguf(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Signals the running instance that an activation needs its attention —
    /// an "Open With" handoff, or a plain second launch. Best-effort: when no
    /// instance is listening the signal is dropped (the handoff file, if any,
    /// is picked up at the end of startup instead).
    /// </summary>
    public static void Signal() => Signal(EventName);

    /// <summary>Testable core of <see cref="Signal()"/> with an explicit event name.</summary>
    public static void Signal(string eventName)
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
    public static EventWaitHandle Listen() => Listen(EventName);

    /// <summary>Testable core of <see cref="Listen()"/> with an explicit event name.</summary>
    public static EventWaitHandle Listen(string eventName) => new(
        false, EventResetMode.AutoReset, eventName, out _);
}
