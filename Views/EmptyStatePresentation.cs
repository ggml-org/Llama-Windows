using LlamaApp.Llama;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from the llama server's state to the Available section's
    /// empty-state text — the placeholder shown while there are no local
    /// models. Kept separate from <see cref="MainWindow"/> so the wording
    /// rules are unit-testable (no XAML objects involved), mirroring
    /// <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class EmptyStatePresentation
    {
        /// <summary>
        /// Returns the empty-state text for the current server/install state.
        /// Every state gets an honest message: previously anything but
        /// <see cref="LlamaManager.ServerState.Running"/> read "Starting the
        /// llama server…" forever — including a crashed server, a failed
        /// install, and the 5-minute give-up path.
        /// </summary>
        public static string Describe(
            LlamaManager.ServerState status,
            LlamaManager.InstallState installState) => status switch
        {
            // Server up, list genuinely empty: point at the browse section
            // below instead of a dead-end "No model yet".
            LlamaManager.ServerState.Running =>
                "No models installed yet — choose a model below to get started",
            LlamaManager.ServerState.Starting =>
                "Starting the llama server…",
            // Crashed / failed to start: say so and point at the footer's
            // relaunch button (visible in exactly this state).
            LlamaManager.ServerState.Failed =>
                "The llama server isn't running — relaunch it with the button below",
            _ => installState switch
            {
                // First-run binary install can pull hundreds of MB with no
                // other progress indication — say what's happening.
                LlamaManager.InstallState.Installing =>
                    "Installing the llama server — the first run can take a few minutes…",
                LlamaManager.InstallState.Failed =>
                    "Couldn't install the llama server — relaunch it with the button below",
                // Stopped + Idle: the brief moment before the ensure pipeline
                // runs at startup.
                _ => "Starting the llama server…",
            },
        };
    }
}
