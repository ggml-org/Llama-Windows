using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// Small, side-effect-free where possible helpers for diagnosing and cleaning
/// up a managed llama-server launch. Kept separate from <see cref="LlamaManager"/>
/// so port-conflict classification and process teardown can be tested without
/// the manager singleton or a real llama installation.
/// </summary>
internal static partial class ServerLaunch
{
    /// <summary>
    /// Recognizes llama.cpp's bind diagnostics as well as the underlying
    /// Windows/POSIX socket errors. In particular, some llama.cpp builds log
    /// the bind error but do not exit, so process exit alone is not sufficient.
    /// </summary>
    internal static bool IsBindFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        return output.Contains("couldn't bind http server socket", StringComparison.OrdinalIgnoreCase)
            || output.Contains("could not bind http server socket", StringComparison.OrdinalIgnoreCase)
            || output.Contains("failed to bind", StringComparison.OrdinalIgnoreCase)
            || output.Contains("bind() failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || output.Contains("eaddrinuse", StringComparison.OrdinalIgnoreCase)
            || output.Contains("wsaeaddrinuse", StringComparison.OrdinalIgnoreCase)
            || output.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The option name from llama.cpp's preset-rejection diagnostic,
    /// <c>option 'name' not recognized in preset '...'</c>, which
    /// <c>llama serve</c> emits when the preset INI carries a key it does not
    /// know. Pure over whatever output tail the caller has collected.
    /// </summary>
    /// <remarks>
    /// Unrelated log lines around the diagnostic — and text after it on the
    /// same line — are fine, but the wording and both quoted segments must be
    /// exact, non-empty, and closed on one line: malformed quoting, bind
    /// errors, and generic unknown-flag failures that lack the
    /// <c>in preset '...'</c> tail do not match. Returns the topmost
    /// rejection and the name verbatim.
    /// </remarks>
    internal static bool TryGetPresetRejection(string? output, [NotNullWhen(true)] out string? option)
    {
        option = null;
        if (string.IsNullOrEmpty(output)) return false;

        var match = PresetRejectionRegex().Match(output);
        if (!match.Success) return false;

        option = match.Groups["option"].Value;
        return true;
    }

    /// <summary>
    /// The diagnostic's line shape. Matched against the whole output rather
    /// than per line: the quoted segments exclude CR and LF, so a rejection
    /// can never be assembled across two log lines and CRLF needs no
    /// splitting. The wording is matched exactly as llama.cpp prints it; the
    /// captured name is verbatim.
    /// </summary>
    [GeneratedRegex(
        @"option '(?<option>[^'\r\n]+)' not recognized in preset '[^'\r\n]+'",
        RegexOptions.CultureInvariant)]
    private static partial Regex PresetRejectionRegex();

    /// <summary>
    /// Whether a failed managed-server launch or preset reload earns the
    /// one-shot retry with the user's <c>models.user.ini</c> overrides
    /// suspended. Pure policy — the recovery mechanics (suspend, kill/reap,
    /// restart) live in <see cref="LlamaManager"/>.
    /// </summary>
    /// <param name="retryAllowed">False inside the retry itself (and on paths
    /// that must never recover, e.g. caller cancellation): recovery is owned
    /// by the ordinary launch/reload attempt, never by its own retry.</param>
    /// <param name="hasActiveOverrides">Only an active user override can carry
    /// an unknown preset key — the app's own keys are always known-good, so
    /// with no active overrides there is nothing worth suspending and the
    /// failure stays an ordinary one.</param>
    /// <param name="rejectedOption">The exact option name from the server's
    /// preset-rejection diagnostic (see <see cref="TryGetPresetRejection"/>).
    /// Generic custom-CLI errors (unknown flag/argument without the
    /// <c>in preset '...'</c> tail) never match and never recover.</param>
    /// <param name="retryAlreadyUsed">The retry is one-shot per fresh start:
    /// a failure the recovery itself produces must not cascade.</param>
    internal static bool ShouldRetryWithDefaults(
        bool retryAllowed, bool hasActiveOverrides, string? rejectedOption, bool retryAlreadyUsed)
        => retryAllowed && hasActiveOverrides && rejectedOption is not null && !retryAlreadyUsed;

    /// <summary>
    /// Whether a failed launch's preset rejection earns the one-shot
    /// suspend-and-retry — the launch path's guard BEFORE any recovery side
    /// effect (suspension, teardown, retry spawn). Cancellation outranks
    /// everything: a cancelled attempt must never mutate the user's override
    /// state or spawn its retry. Pure so the guard, and the cancellation rule
    /// in particular, is testable without a process.
    /// </summary>
    internal static bool ShouldRecoverFromLaunchRejection(
        bool retryAllowed, bool hasActiveOverrides, string? rejectedOption, bool cancellationRequested)
        => !cancellationRequested
           && ShouldRetryWithDefaults(retryAllowed, hasActiveOverrides, rejectedOption, retryAlreadyUsed: false);

    /// <summary>
    /// Whether the staged user-override state may be published as accepted
    /// (the <c>LlamaManager.PublishOverrides</c> step): the regeneration must
    /// have landed on disk (a failed write leaves the server on stale bytes —
    /// nothing newer may be claimed), the configuration must have been
    /// accepted, AND the server must actually read the app's
    /// <c>--models-preset</c> file. An adopted external server runs no preset
    /// of ours — its reload is a plain rescan that applies none of the
    /// overrides, so success there must never publish or claim override
    /// application. Pure so the adopted-server outcome is testable without a
    /// server.
    /// </summary>
    internal static bool ShouldPublishOverrides(
        bool writeSucceeded, bool reloadAccepted, bool serverUsesModelPresets)
        => writeSucceeded && reloadAccepted && serverUsesModelPresets;

    /// <summary>
    /// Whether a completed preset regeneration counts as a successful
    /// <c>ReloadModelPresetsAsync</c> (its <c>bool</c> contract with the
    /// Settings UI): the write must have landed — a failed write leaves the
    /// server on stale bytes — and a running server must have accepted the
    /// reload OR had its rejection recovered (the overrides were suspended and
    /// the server restarted, which is the recovery succeeding). A stopped
    /// server is deferred, not failed: the bytes are in place and take effect
    /// on the next start. Pure so the semantics are testable without a server.
    /// </summary>
    internal static bool ReloadSucceeded(
        bool writeSucceeded, bool serverRunning, bool reloadAccepted, bool recovered = false)
        => writeSucceeded && (!serverRunning || reloadAccepted || recovered);

    /// <summary>A user-actionable conflict message for both the app and a manual CLI launch.</summary>
    internal static string PortInUseMessage(string listenAddress, int port) =>
        $"The llama server could not listen on http://{listenAddress}:{port} because that port is already in use. " +
        "Stop the other server or process using this port, or choose a different port in Llama settings. " +
        "For a manual llama CLI launch, pass --port <free-port>.";

    /// <summary>A bounded-startup message; health, not a log line, defines readiness.</summary>
    internal static string TimeoutMessage(string listenAddress, int port, TimeSpan timeout) =>
        $"The llama server did not become ready on http://{listenAddress}:{port} within {timeout.TotalSeconds:0} seconds " +
        "(no successful health check). Check that the configured port is free and review the llama server log. " +
        "Choose a different port in Llama settings, or pass --port <free-port> for a manual llama CLI launch.";

    /// <summary>
    /// Whether an existing IPv4 listener would prevent llama.cpp binding the
    /// requested endpoint. This is a preflight only: the child still owns the
    /// authoritative bind, closing the check/launch race without reserving a
    /// socket or briefly exposing a listener of our own.
    /// </summary>
    internal static bool IsPortOccupied(string listenAddress, int port)
    {
        if (!IPAddress.TryParse(listenAddress, out var requested) ||
            requested.AddressFamily != AddressFamily.InterNetwork)
            return false;

        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => ListenersConflict(requested, endpoint, port));
        }
        catch (Exception ex)
        {
            // Network-interface enumeration is advisory. If Windows cannot
            // supply it, launch normally and classify the server's own error.
            Log.Debug($"server port preflight unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Pure endpoint-conflict rule, internal for focused tests.</summary>
    internal static bool ListenersConflict(IPAddress requested, IPEndPoint listener, int port)
    {
        if (listener.Port != port || listener.AddressFamily != AddressFamily.InterNetwork)
            return false;

        // A bind-all listener conflicts with every specific IPv4 bind, and a
        // new bind-all listener conflicts with every existing IPv4 listener.
        // Distinct specific interface addresses can otherwise share a port.
        return IPAddress.Any.Equals(requested)
            || IPAddress.Any.Equals(listener.Address)
            || requested.Equals(listener.Address);
    }

    /// <summary>
    /// Kills exactly the supplied managed process and its descendants, then
    /// waits boundedly for the OS to reap it and release its listening socket.
    /// Kill alone only requests termination; without the wait a retry can
    /// preflight the old listener, and a failed launch can appear to return
    /// while its child is still alive.
    /// </summary>
    internal static void TerminateProcessTree(Process process, TimeSpan wait)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit((int)wait.TotalMilliseconds))
                Log.Warn($"timed out waiting for llama server process {process.Id} to exit after kill");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "best-effort server kill failed");
        }
    }
}
