namespace LlamaApp;

/// <summary>The operation selected for a progress-toast report.</summary>
internal enum ProgressToastAction
{
    Show,
    Update,
    Ignore,
}

/// <summary>
/// Lifecycle state for one download's progress toast. A transient close only
/// hides the toast; a terminal close seals it against callbacks already queued
/// on the dispatcher. Suppression is retained after a user dismissal until a
/// new download attempt creates a fresh state.
/// </summary>
internal sealed class ProgressToastState
{
    private uint _sequence;

    public bool IsShown { get; private set; }
    public bool IsSuppressed { get; private set; }
    public bool IsTerminal { get; private set; }
    public uint Generation { get; private set; }

    public ProgressToastAction NextAction()
    {
        if (IsTerminal || IsSuppressed) return ProgressToastAction.Ignore;
        return IsShown ? ProgressToastAction.Update : ProgressToastAction.Show;
    }

    /// <summary>Returns a non-zero, monotonically increasing sequence number.</summary>
    public uint NextSequence()
    {
        // Zero is invalid and wrapping would make the platform discard updates.
        if (_sequence < uint.MaxValue) _sequence++;
        return _sequence;
    }

    public void MarkShown() => IsShown = true;

    /// <summary>Stops all further reports until a new download attempt.</summary>
    public void Suppress()
    {
        IsSuppressed = true;
        IsShown = false;
    }

    /// <summary>Hides the toast; terminal hides also reject late reports.</summary>
    public void Close(bool terminal)
    {
        IsShown = false;
        Generation = unchecked(Generation + 1);
        if (terminal) IsTerminal = true;
    }
}
