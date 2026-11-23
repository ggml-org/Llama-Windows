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
        /// <para><paramref name="failureMessage"/> is
        /// <see cref="LlamaManager.FailureMessage"/> — the user-facing reason
        /// behind a Failed state. It was set by the manager but never read
        /// anywhere; the dot's tooltip is the one place a user goes to ask
        /// "why is it red?", so the reason is included there.</para>
        /// <para><paramref name="suspendedPresetOption"/> is
        /// <see cref="UserModelOverrides.SuspendedOption"/> — the option the
        /// server refused, which suspended the per-model preset overrides. It
        /// is a warning, kept separate from <paramref name="failureMessage"/>:
        /// when the server is running it turns the dot amber and the tooltip
        /// says the server is running as-is (no relaunch); when the server is
        /// down the failure rendering stands unchanged and the warning is
        /// appended beside the failure reason.</para>
        /// </summary>
        public static Description Describe(
            LlamaManager.ServerState status,
            LlamaManager.InstallState installState,
            string? failureMessage = null,
            bool runtimeAlreadyInstalled = false,
            string? suspendedPresetOption = null)
        {
            var detail = Sanitize(failureMessage);
            // The suspended option is a CLI option name, not a message — trim
            // and truncate only (Sanitize would sentence-case it with a period).
            var warning = suspendedPresetOption?.Trim();
            if (string.IsNullOrEmpty(warning)) warning = null;
            else if (warning.Length > 120) warning = warning[..120] + "…";
            return status switch
            {
                LlamaManager.ServerState.Running =>
                    warning is null
                        ? new(Green, "llama server: running", false)
                        : new(Amber,
                            $"llama server: running — model overrides are ignored because {warning} is not a known option in {UserModelOverrides.FileName}. Fix it in Settings → Llama; the server keeps running without a relaunch.",
                            false),
                LlamaManager.ServerState.Starting =>
                    new(Amber, "llama server: starting…" + WarningSuffix(warning), false),
                LlamaManager.ServerState.Failed =>
                    new(Red,
                        (detail is null
                            ? "llama server: not running (crashed or failed to start) — use the relaunch button"
                            : $"llama server: not running — {detail} Use the relaunch button.")
                        + WarningSuffix(warning),
                        true),
                _ => installState switch
                {
                    // First-run binary install: amber, and say what's happening —
                    // the download can be hundreds of MB and otherwise has no
                    // progress indication anywhere in the app.
                    LlamaManager.InstallState.Installing when runtimeAlreadyInstalled =>
                        new(Amber, "llama server: updating llama.cpp…" + WarningSuffix(warning), false),
                    LlamaManager.InstallState.Installing =>
                        new(Amber, "llama server: installing llama.cpp — the first run can take a few minutes…" + WarningSuffix(warning), false),
                    LlamaManager.InstallState.Failed =>
                        new(Gray,
                            (detail is null
                                ? "llama server: stopped (install failed) — use the relaunch button"
                                : $"llama server: stopped — install failed: {detail} Use the relaunch button.")
                            + WarningSuffix(warning),
                            true),
                    _ => new(Gray, "llama server: stopped" + WarningSuffix(warning), false),
                },
            };
        }

        /// <summary>
        /// The tooltip fragment naming the suspended preset override warning,
        /// appended to tooltips that have nothing to say about it. Empty when
        /// there is no warning, keeping the no-warning tooltips byte-for-byte
        /// identical to the warning-free rendering.
        /// </summary>
        private static string WarningSuffix(string? warning) =>
            warning is null
                ? ""
                : $" Note: model overrides are ignored because {warning} is not a known option in {UserModelOverrides.FileName}.";

        /// <summary>
        /// Prepares a raw failure message for a tooltip: first line only
        /// (an install exception can span several), truncated, and guaranteed
        /// to end with a period so the "Use the relaunch button." follow-up
        /// reads as a separate sentence. Null/blank → null (no detail).
        /// </summary>
        private static string? Sanitize(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            var firstLine = message.Split('\n')[0].Trim().TrimEnd('\r');
            const int Max = 120;
            if (firstLine.Length > Max)
                firstLine = firstLine[..Max] + "…";
            return firstLine.EndsWith('.') || firstLine.EndsWith('…')
                ? firstLine
                : firstLine + ".";
        }
    }
}
