using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LlamaApp.Common;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Regression coverage for managed-server startup failures. These tests do not
/// require llama.exe: the occupied endpoint is real, the bind output is the
/// shape emitted by llama.cpp, and teardown is exercised with a fixed argument
/// list (no shell) against an OS sleep process.
/// </summary>
public sealed class ServerLaunchTests
{
    [Fact]
    public void OccupiedLoopbackPort_IsDetectedWithoutTakingOverTheListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(ServerLaunch.IsPortOccupied(ListenAddresses.Localhost, port));
            Assert.True(ServerLaunch.IsPortOccupied(ListenAddresses.AllInterfaces, port));

            // Preflight only observes the socket table; it must not stop or
            // otherwise disturb the process that owns the occupied endpoint.
            Assert.Equal(port, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ListenerConflict_RespectsSpecificInterfacesAndIgnoresIpv6()
    {
        var port = 49331;
        var loopback = new IPEndPoint(IPAddress.Loopback, port);
        var otherInterface = new IPEndPoint(IPAddress.Parse("192.0.2.10"), port);
        var ipv6Any = new IPEndPoint(IPAddress.IPv6Any, port);

        Assert.True(ServerLaunch.ListenersConflict(IPAddress.Loopback, loopback, port));
        Assert.True(ServerLaunch.ListenersConflict(IPAddress.Any, otherInterface, port));
        Assert.False(ServerLaunch.ListenersConflict(IPAddress.Loopback, otherInterface, port));
        Assert.False(ServerLaunch.ListenersConflict(IPAddress.Any, ipv6Any, port));
        Assert.False(ServerLaunch.ListenersConflict(IPAddress.Loopback, loopback, port + 1));
    }

    [Theory]
    [InlineData("0.02.238.560 E srv start: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 9931")]
    [InlineData("listen tcp 127.0.0.1:9931: bind: address already in use")]
    [InlineData("bind() failed with error: WSAEADDRINUSE")]
    [InlineData("Only one usage of each socket address (protocol/network address/port) is normally permitted")]
    public void BindFailure_IsRecognized(string stderr)
    {
        Assert.True(ServerLaunch.IsBindFailure(stderr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.02.100.000 I srv llama_server: listening on http://127.0.0.1:9931")]
    [InlineData("model loading failed because an address was invalid")]
    public void UnrelatedOutput_IsNotABindFailure(string? stderr)
    {
        Assert.False(ServerLaunch.IsBindFailure(stderr));
    }

    [Fact]
    public void BindFailureWithoutListeningLine_ProducesActionablePortError()
    {
        const string stderr = """
            0.00.012.342 I srv llama_server: initializing ...
            0.02.238.560 E srv start: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 49331
            0.02.239.489 I srv operator(): cleaning up before exit...
            """;
        Assert.DoesNotContain("listening on http", stderr, StringComparison.OrdinalIgnoreCase);

        var message = LlamaManager.FormatStartFailureMessage(
            stderr, 49331, ListenAddresses.Localhost, timedOut: false);

        Assert.Contains("49331", message);
        Assert.Contains("already in use", message);
        Assert.Contains("Stop the other server or process", message);
        Assert.Contains("different port", message);
        Assert.Contains("--port <free-port>", message);
    }

    [Fact]
    public void TimeoutWithoutListeningLine_IsExplicitAndKeepsStderrDetail()
    {
        const string stderr = """
            0.00.012.342 I srv llama_server: initializing ...
            0.02.100.000 I srv llama_server: still initializing, no listener yet
            """;
        Assert.DoesNotContain("listening on http", stderr, StringComparison.OrdinalIgnoreCase);

        var message = LlamaManager.FormatStartFailureMessage(
            stderr, 49331, ListenAddresses.Localhost, timedOut: true);

        Assert.Contains("within 15 seconds", message);
        Assert.Contains("no successful health check", message);
        Assert.Contains("49331", message);
        Assert.Contains("still initializing, no listener yet", message);
        Assert.Contains("different port", message);
    }

    [Fact]
    public void FailedLaunchCleanup_KillsAndReapsTheSpawnedProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe")
            : new ProcessStartInfo("/bin/sleep");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("30");
        }
        else
        {
            psi.ArgumentList.Add("30");
        }

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        try
        {
            Assert.False(process.HasExited);

            ServerLaunch.TerminateProcessTree(process, TimeSpan.FromSeconds(2));

            // TerminateProcessTree must wait, not merely issue Kill and return
            // with an orphan that can collide with the next launch.
            Assert.True(process.HasExited);
            Assert.True(process.WaitForExit(0));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
    }
}
