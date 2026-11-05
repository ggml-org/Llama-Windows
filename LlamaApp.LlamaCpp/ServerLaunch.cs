using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// Small, side-effect-free where possible helpers for diagnosing and cleaning
/// up a managed llama-server launch. Kept separate from <see cref="LlamaManager"/>
/// so port-conflict classification and process teardown can be tested without
/// the manager singleton or a real llama installation.
/// </summary>
internal static class ServerLaunch
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
