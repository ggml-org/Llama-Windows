namespace LlamaApp.Views;

/// <summary>
/// Decides which server-listed ids count as "actually present" for the row
/// list. The server's catalog can outlive the disk — a preset entry keeps a
/// deleted model listed (a ghost), and rendering such rows shows users a
/// model they can see but never use.
///
/// <para>Why this isn't simply "hide what we can't resolve": hiding has
/// risks, and they are managed here rather than ignored. A model in a
/// location the app doesn't know (an adopted external server's own model
/// dir, an exotic cache layout) hides until resolution succeeds — but
/// nothing is lost (the model stays loadable, and the row returns the
/// moment it resolves) and a briefly unavailable file (an asleep network
/// share) is filtered out by the caller's snapshot hysteresis, not here.
/// Busy models always count as present: a mid-download has no file yet, a
/// loading/loaded model may have been deleted under a running child —
/// either way its row must stay. Pure for tests.</para>
/// </summary>
public static class ModelPresence
{
    /// <summary>
    /// The ids that keep their rows: everything the server reports as busy
    /// (<paramref name="isBusy"/>) plus everything with a file the app can
    /// find (<paramref name="fileExists"/>). The complement — listed but
    /// fileless and idle — is the ghost class.
    /// </summary>
    public static HashSet<string> EffectivelyPresent(
        IEnumerable<string> serverIds,
        Func<string, bool> isBusy,
        Func<string, bool> fileExists)
        => serverIds
            .Where(id => isBusy(id) || fileExists(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
