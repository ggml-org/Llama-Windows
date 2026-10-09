using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the Settings "listen on" list: the two pseudo-addresses every
/// deployment needs (all interfaces, localhost) are always present, real
/// interfaces are sorted and de-duplicated, and the live OS enumeration only
/// ever yields IPv4 addresses the server could actually bind.
/// </summary>
public sealed class ListenAddressesTests
{
    [Fact]
    public void BuildList_AlwaysStartsWithAllInterfacesAndLocalhost()
    {
        var list = ListenAddresses.BuildList(new[]
        {
            new ListenAddress("Ethernet", "192.168.1.10"),
        });

        Assert.Equal("All interfaces", list[0].Name);
        Assert.Equal(ListenAddresses.AllInterfaces, list[0].Address);
        Assert.Equal("Localhost", list[1].Name);
        Assert.Equal(ListenAddresses.Localhost, list[1].Address);
    }

    [Fact]
    public void BuildList_IncludesGivenInterfaces()
    {
        var list = ListenAddresses.BuildList(new[]
        {
            new ListenAddress("Wi-Fi", "192.168.1.42"),
            new ListenAddress("Ethernet", "10.0.0.5"),
        });

        Assert.Contains(list, e => e.Name == "Wi-Fi" && e.Address == "192.168.1.42");
        Assert.Contains(list, e => e.Name == "Ethernet" && e.Address == "10.0.0.5");
    }

    [Fact]
    public void BuildList_SortsInterfacesByNameThenAddress()
    {
        var list = ListenAddresses.BuildList(new[]
        {
            new ListenAddress("Wi-Fi", "192.168.1.42"),
            new ListenAddress("Ethernet", "10.0.0.5"),
            new ListenAddress("Ethernet", "10.0.0.4"),
        });

        // After the two pseudo-addresses, the interfaces are name-then-IP.
        Assert.Equal(new[] { "All interfaces", "Localhost", "Ethernet", "Ethernet", "Wi-Fi" },
            list.Select(e => e.Name));
        Assert.Equal(new[] { "10.0.0.4", "10.0.0.5" },
            list.Skip(2).Take(2).Select(e => e.Address));
    }

    [Fact]
    public void BuildList_RemovesDuplicateAddresses()
    {
        var list = ListenAddresses.BuildList(new[]
        {
            new ListenAddress("Wi-Fi", "192.168.1.42"),
            new ListenAddress("Ethernet", "192.168.1.42"),
        });

        Assert.Single(list, e => e.Address == "192.168.1.42");
    }

    [Fact]
    public void BuildList_DropsBlankEntries()
    {
        var list = ListenAddresses.BuildList(new[]
        {
            new ListenAddress("", "1.2.3.4"),
            new ListenAddress("Wi-Fi", ""),
            new ListenAddress("   ", "5.6.7.8"),
        });

        Assert.Equal(2, list.Count); // the two pseudo-addresses only
        Assert.All(list, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        Assert.All(list, e => Assert.False(string.IsNullOrWhiteSpace(e.Address)));
    }

    [Fact]
    public void List_AlwaysContainsAllInterfacesAndLocalhost()
    {
        var list = ListenAddresses.List();

        Assert.Contains(list, e => e.Address == ListenAddresses.AllInterfaces);
        Assert.Contains(list, e => e.Address == ListenAddresses.Localhost);
    }

    [Fact]
    public void List_OnlyYieldsIpv4Addresses()
    {
        var list = ListenAddresses.List();

        Assert.All(list, e =>
            Assert.True(
                System.Net.IPAddress.TryParse(e.Address, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
                $"not IPv4: {e.Address}"));
    }

    // ---- Resolve: startup validation of a persisted address ----

    [Fact]
    public void Resolve_Blank_FallsBackToLoopback()
    {
        var r = ListenAddresses.Resolve(null, []);
        Assert.Equal(ListenAddresses.Localhost, r.Address);
        Assert.True(r.FellBack);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.5")]   // any loopback address is always local
    [InlineData("127.0.0.1:9931")] // not an IP literal -> handled below
    public void Resolve_PseudoAndLoopback_AreAlwaysValid(string configured)
    {
        var r = ListenAddresses.Resolve(configured, []);
        if (configured.Contains(':'))
        {
            // Not a parseable IPv4 literal: conservative fallback.
            Assert.True(r.FellBack);
            Assert.Equal(ListenAddresses.Localhost, r.Address);
        }
        else
        {
            Assert.False(r.FellBack);
            Assert.Equal(configured, r.Address);
        }
    }

    [Fact]
    public void Resolve_AssignedInterface_IsKept()
    {
        var available = ListenAddresses.BuildList(new[] { new ListenAddress("Wi-Fi", "192.168.1.42") });

        var r = ListenAddresses.Resolve("192.168.1.42", available);

        Assert.False(r.FellBack);
        Assert.Equal("192.168.1.42", r.Address);
    }

    [Fact]
    public void Resolve_UnassignedInterface_FallsBackToLoopback()
    {
        // The classic DHCP-change / different-network case: the address is a
        // valid IPv4 literal but no longer belongs to this machine.
        var available = ListenAddresses.BuildList(new[] { new ListenAddress("Wi-Fi", "192.168.1.10") });

        var r = ListenAddresses.Resolve("192.168.1.42", available);

        Assert.True(r.FellBack);
        Assert.Equal(ListenAddresses.Localhost, r.Address);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("not-an-ip")]
    [InlineData("999.1.1.1")]
    [InlineData("192.168.1.42 ")]
    public void Resolve_InvalidValues_FallBackToLoopback(string configured)
    {
        var r = ListenAddresses.Resolve(configured, []);
        Assert.True(r.FellBack);
        Assert.Equal(ListenAddresses.Localhost, r.Address);
    }
}
