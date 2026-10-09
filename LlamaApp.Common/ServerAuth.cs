using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LlamaApp.Common;

/// <summary>
/// Access control for the local llama server. The server has no authentication
/// by default, so binding it to anything other than loopback publishes a
/// control API (model download/load/delete and chat) to the network. When the
/// user chooses a non-loopback address the app generates a key, hands it to
/// the server via a per-user key file (<c>--api-key-file</c> — never an argv
/// flag, which any local user could read via WMI), and sends it as a Bearer
/// token on every request.
/// </summary>
public static class ServerAuth
{
    /// <summary>
    /// True when <paramref name="listenAddress"/> reaches beyond this machine,
    /// so the server needs an API key: any parseable IPv4 that is not loopback
    /// (this includes <c>0.0.0.0</c> and every specific interface address).
    /// Unparseable/blank values behave like the loopback default.
    /// </summary>
    public static bool RequiresApiKey(string? listenAddress)
    {
        if (string.IsNullOrWhiteSpace(listenAddress)) return false;
        if (!IPAddress.TryParse(listenAddress, out var ip)) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        return !IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// A fresh 256-bit key as 64 lower-case hex characters. Hex avoids shell
    /// and URL quoting surprises when the key is pasted into a request or a
    /// query string.
    /// </summary>
    public static string GenerateApiKey()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
