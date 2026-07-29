using LlamaApp.Llama;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from the llama server's state to the footer's status
    /// indicator — the dot color, its tooltip, and whether the relaunch
    /// button is shown. Kept separate from <see cref="MainWindow"/> so the
    /// rules are unit-testable (no XAML objects involved:
    /// <see cref="Windows.UI.Color"/> is a plain struct).
    /// </summary>
    public static class ServerStatusPresentation
    {
        /// <summary>The rendered footer status: dot color, dot tooltip, and
        /// relaunch-button visibility.</summary>
        public readonly record struct Description(
            Windows.UI.Color Dot, string ToolTip, bool CanRelaunch);

        // GitHub-style palette; the red matches the delete flyout's #F85149.
        // The struct is initialized field-by-field rather than via
        // Microsoft.UI.ColorHelper.FromArgb on purpose: ColorHelper is a WinRT
        // projected class that needs the Windows App SDK runtime, which the
        // plain unit-test host doesn't have ("class not registered") — the
        // Color struct itself is pure managed data.
        private static readonly Windows.UI.Color Green =
            new() { A = 255, R = 0x3F, G = 0xB9, B = 0x50 };
        private static readonly Windows.UI.Color Amber =
            new() { A = 255, R = 0xD2, G = 0x99, B = 0x22 };
        private static readonly Windows.UI.Color Red =
            new() { A = 255, R = 0xF8, G = 0x51, B = 0x49 };
        private static readonly Windows.UI.Color Gray =
            new() { A = 255, R = 0x8B, G = 0x94, B = 0x9E };

        /// <summary>
        /// Maps <paramref name="status"/> (plus <paramref name="installState"/>
        /// for the one case Stopped alone can't express — a failed first-run
        /// install) to the footer rendering. The relaunch button shows only
        /// when the server is down through no transient cause: crashed
        /// (<see cref="LlamaManager.ServerState.Failed"/>) or a failed
        /// install. It never shows during the startup
        /// Stopped → Starting → Running sequence, so it doesn't flash at
        /// launch.
        /// </summary>
        public static Description Describe(
            LlamaManager.ServerState status,
            LlamaManager.InstallState installState) => status switch
        {
            LlamaManager.ServerState.Running =>
                new(Green, "llama server: running", false),
            LlamaManager.ServerState.Starting =>
                new(Amber, "llama server: starting…", false),
            LlamaManager.ServerState.Failed =>
                new(Red, "llama server: not running (crashed or failed to start) — use the relaunch button", true),
            _ => installState == LlamaManager.InstallState.Failed
                ? new(Gray, "llama server: stopped (install failed) — use the relaunch button", true)
                : new(Gray, "llama server: stopped", false),
        };
    }
}
