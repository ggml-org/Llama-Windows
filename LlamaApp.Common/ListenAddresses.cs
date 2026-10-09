using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LlamaApp.Common;

/// <summary>
/// One entry in the Settings "listen on" list: a human-readable interface
/// name plus the IPv4 address the llama server would bind to.
/// </summary>
public sealed record ListenAddress(string Name, string Address);

/// <summary>
/// Enumerates the local network interfaces the llama server can listen on.
/// The list always leads with the two pseudo-addresses every deployment
/// cares about — <c>0.0.0.0</c> (all interfaces) and <c>127.0.0.1</c>
/// (localhost) — followed by the machine's real, up, non-loopback IPv4
/// interfaces, sorted by name. IPv6 is deliberately left out: the llama
/// server binds IPv4, and mixing families in one list invites a pick that
/// can never bind.
/// </summary>
public static class ListenAddresses
{
    /// <summary>Bind-all pseudo-address; the server listens on every IPv4 interface.</summary>
    public const string AllInterfaces = "0.0.0.0";

    /// <summary>Loopback address; the server is reachable only from this machine.</summary>
    public const string Localhost = "127.0.0.1";

    /// <summary>
    /// The addresses to show in Settings, live from the OS. Best-effort: a
    /// failed interface enumeration still yields the two pseudo-addresses —
    /// the app keeps working, just without per-interface choices.
    /// </summary>
    public static IReadOnlyList<ListenAddress> List()
    {
        var interfaces = new List<ListenAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                // The loopback interface is already covered by the explicit
                // Localhost entry; listing it again would only add noise.
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    interfaces.Add(new ListenAddress(nic.Name, addr.Address.ToString()));
                }
            }
        }
        catch
        {
            // Interface enumeration is best-effort — the two pseudo-addresses
            // below are enough to keep the setting usable.
        }
        return BuildList(interfaces);
    }

    /// <summary>
    /// Pure list construction, unit-testable without touching the OS: prepends
    /// the two pseudo-addresses, drops blank entries, sorts by name then
    /// address, and removes duplicate addresses (an interface with several
    /// IPv4 addresses contributes one row per address, never two rows with
    /// the same address).
    /// </summary>
    public static IReadOnlyList<ListenAddress> BuildList(IEnumerable<ListenAddress> interfaces)
    {
        var result = new List<ListenAddress>
        {
            new("All interfaces", AllInterfaces),
            new("Localhost", Localhost),
        };

        foreach (var entry in interfaces
                     .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Address))
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.Address, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Any(e => e.Address.Equals(entry.Address, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(entry);
        }

        return result;
    }
}
