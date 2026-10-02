namespace LlamaApp;

/// <summary>
/// Maps a runtime update check outcome
/// (<see cref="Llama.RuntimeUpdateScheduler.CheckOutcome"/>) to the one-line
/// status the Settings card shows after a check. Kept here — a pure function,
/// not a view — so the wording is unit-testable alongside the scheduler's
/// decision logic.
/// </summary>
internal static class RuntimeUpdateMessages
{
    /// <summary>The status line for a completed check; empty for NotDue
    /// (a manual check can never be not-due, and the card says nothing).</summary>
    public static string Describe(Llama.RuntimeUpdateScheduler.CheckOutcome outcome) => outcome switch
    {
        Llama.RuntimeUpdateScheduler.CheckOutcome.UpToDate =>
            "llama.cpp is up to date.",
        Llama.RuntimeUpdateScheduler.CheckOutcome.Installed =>
            "Updated — the new version is used the next time the server starts.",
        Llama.RuntimeUpdateScheduler.CheckOutcome.InstallFailed =>
            "The update failed — the log has details. It will be retried next week.",
        Llama.RuntimeUpdateScheduler.CheckOutcome.DeferredServerBusy =>
            "An update is available, but the server is running. It installs at the next launch.",
        Llama.RuntimeUpdateScheduler.CheckOutcome.FetchFailed =>
            "Couldn't reach GitHub — check your connection and try again.",
        Llama.RuntimeUpdateScheduler.CheckOutcome.SkippedNotManaged =>
            "This llama.cpp installation is your own (found on PATH) — Llama leaves it alone.",
        Llama.RuntimeUpdateScheduler.CheckOutcome.SkippedUnparsableVersion =>
            "The installed runtime doesn't report a comparable version.",
        _ => "",
    };
}
