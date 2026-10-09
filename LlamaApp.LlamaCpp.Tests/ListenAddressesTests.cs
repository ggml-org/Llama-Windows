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
}
