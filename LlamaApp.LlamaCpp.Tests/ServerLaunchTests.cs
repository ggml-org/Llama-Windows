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

    [Theory]
    [InlineData("option 'temperture' not recognized in preset 'model-context-presets.ini'", "temperture")]
    [InlineData("option 'temperture' not recognized in preset 'model-context-presets.ini'\n", "temperture")]
    [InlineData("0.02.238.560 E srv start: option 'num_gpu_layers' not recognized in preset 'model-context-presets.ini' (line 7)", "num_gpu_layers")]
    [InlineData("option 'ctx-size-override' not recognized in preset 'user-model-overrides.ini'", "ctx-size-override")]
    public void PresetRejection_ReturnsTheOptionName(string stderr, string expected)
    {
        Assert.True(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Equal(expected, option);
    }

    [Fact]
    public void PresetRejection_IsFoundAmongSurroundingLogLines()
    {
        const string stderr = """
            0.00.012.342 I srv llama_server: initializing ...
            0.02.238.560 E srv start: option 'temperture' not recognized in preset 'model-context-presets.ini'
            0.02.239.489 I srv operator(): cleaning up before exit...
            """;

        Assert.True(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Equal("temperture", option);
    }

    [Fact]
    public void PresetRejection_IsFoundWithCrlfLineEndings()
    {
        var stderr = "0.00.012.342 I srv llama_server: initializing ...\r\n"
            + "0.02.238.560 E srv start: option 'temperture' not recognized in preset 'model-context-presets.ini'\r\n"
            + "0.02.239.489 I srv operator(): cleaning up before exit...\r\n";

        Assert.True(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Equal("temperture", option);
    }

    [Fact]
    public void MultiplePresetRejections_ReturnsTheFirstOptionName()
    {
        const string stderr = """
            option 'alpha' not recognized in preset 'model-context-presets.ini'
            option 'beta' not recognized in preset 'model-context-presets.ini'
            """;

        Assert.True(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Equal("alpha", option);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void EmptyOrNullOutput_IsNotAPresetRejection(string? stderr)
    {
        Assert.False(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Null(option);
    }

    [Theory]
    [InlineData("option 'temperture not recognized in preset 'model-context-presets.ini'")]
    [InlineData("option 'temperture' not recognized in preset 'model-context-presets.ini")]
    [InlineData("option 'temperture' not recognized in preset model-context-presets.ini'")]
    [InlineData("option 'temperture'\r\nnot recognized in preset 'model-context-presets.ini'")]
    [InlineData("option 'temperture' not recognized in preset '\r\nmodel-context-presets.ini'")]
    public void MalformedQuoting_IsNotAPresetRejection(string stderr)
    {
        Assert.False(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Null(option);
    }

    [Theory]
    [InlineData("0.02.238.560 E srv start: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 9931")]
    [InlineData("bind() failed with error: WSAEADDRINUSE")]
    [InlineData("error: unknown argument '--temperture'")]
    [InlineData("error: unrecognized flag '--temperture'")]
    [InlineData("option 'temperture' not recognized")]
    [InlineData("option 'temperture' not recognized in preset")]
    [InlineData("option temperture not recognized in preset model-context-presets.ini")]
    [InlineData("option \"temperture\" not recognized in preset \"model-context-presets.ini\"")]
    [InlineData("option 'temperture' not recognized in presets 'model-context-presets.ini'")]
    [InlineData("option '' not recognized in preset 'model-context-presets.ini'")]
    [InlineData("option 'temperture' not recognized in preset ''")]
    [InlineData("OPTION 'temperture' NOT RECOGNIZED IN PRESET 'model-context-presets.ini'")]
    [InlineData("0.02.100.000 I srv llama_server: listening on http://127.0.0.1:9931")]
    public void UnrelatedOutput_IsNotAPresetRejection(string? stderr)
    {
        Assert.False(ServerLaunch.TryGetPresetRejection(stderr, out var option));
        Assert.Null(option);
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

    // ---- preset-rejection recovery policy (pure) ----

    [Theory]
    // The ordinary attempt owns the recovery: rejection + active overrides →
    // exactly one retry.
    [InlineData(true, true, "ctx-size-override", false, true)]
    // The retry itself never recovers again (ownership: no third spawn).
    [InlineData(false, true, "ctx-size-override", false, false)]
    // No active user overrides → nothing to suspend; failure stays ordinary.
    [InlineData(true, false, "ctx-size-override", false, false)]
    // No exact rejection → never recover.
    [InlineData(true, true, null, false, false)]
    // The one-shot is spent → never a second cascade.
    [InlineData(true, true, "ctx-size-override", true, false)]
    public void ShouldRetryWithDefaults_Matrix(
        bool retryAllowed, bool hasActiveOverrides, string? rejectedOption, bool retryAlreadyUsed, bool expected)
    {
        Assert.Equal(expected, ServerLaunch.ShouldRetryWithDefaults(
            retryAllowed, hasActiveOverrides, rejectedOption, retryAlreadyUsed));
    }

    [Theory]
    // The launch path's pre-side-effect guard: a clean rejection earns the
    // one-shot suspend-and-retry.
    [InlineData(true, true, "temperture", false, true)]
    // Cancellation outranks everything: no suspension and no retry even for a
    // perfect rejection — a cancelled attempt must never mutate override
    // state.
    [InlineData(true, true, "temperture", true, false)]
    [InlineData(true, true, null, true, false)]
    // The retry itself never recovers again.
    [InlineData(false, true, "temperture", false, false)]
    // Nothing to suspend / no exact rejection → never recover.
    [InlineData(true, false, "temperture", false, false)]
    [InlineData(true, true, null, false, false)]
    public void ShouldRecoverFromLaunchRejection_Matrix(
        bool retryAllowed, bool hasActiveOverrides, string? rejectedOption,
        bool cancellationRequested, bool expected)
    {
        Assert.Equal(expected, ServerLaunch.ShouldRecoverFromLaunchRejection(
            retryAllowed, hasActiveOverrides, rejectedOption, cancellationRequested));
    }

    [Fact]
    public void StartupRecovery_DecidesFromTheLatch_EvenAfterTheLineIsEvicted()
    {
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add("0.02.238.560 E srv start: option 'temperture' not recognized in preset 'model-context-presets.ini'");
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");

        // The bounded startup Snapshot no longer carries the diagnostic...
        Assert.DoesNotContain("not recognized", tail.Snapshot());
        // ...but the decision reads the latch, which outlives the eviction.
        var rejected = tail.LatestRejection?.Option;
        Assert.Equal("temperture", rejected);
        Assert.True(ServerLaunch.ShouldRecoverFromLaunchRejection(
            retryAllowed: true, hasActiveOverrides: true,
            rejected, cancellationRequested: false));
    }

    [Fact]
    public void StartupRecovery_IsDeclined_WithoutALatchedRejection()
    {
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add("0.02.238.560 E srv start: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 9931");

        // A bind failure latches no rejection: the decision must decline
        // rather than invent one from the snapshot.
        var rejected = tail.LatestRejection?.Option;
        Assert.Null(rejected);
        Assert.False(ServerLaunch.ShouldRecoverFromLaunchRejection(
            retryAllowed: true, hasActiveOverrides: true,
            rejected, cancellationRequested: false));
    }

    // ---- publish rule: accepted AND actually the app's preset server (pure) ----

    [Theory]
    // App-managed preset server accepted the regenerated file: publish.
    [InlineData(true, true, true, true)]
    // Adopted external server (no --models-preset of ours): an accepted reload
    // applies none of the overrides — never publish, never claim.
    [InlineData(true, true, false, false)]
    // Nothing was accepted: nothing published.
    [InlineData(true, false, true, false)]
    // The write never landed: the server accepted stale bytes — the staged
    // state is not what is running.
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, false)]
    public void ShouldPublishOverrides_Matrix(
        bool writeSucceeded, bool reloadAccepted, bool serverUsesModelPresets, bool expected)
    {
        Assert.Equal(expected, ServerLaunch.ShouldPublishOverrides(
            writeSucceeded, reloadAccepted, serverUsesModelPresets));
    }

    [Fact]
    public void AdoptedServer_ReloadCountsAsSuccess_ButNeverPublishesOverrides()
    {
        // ReloadModelPresetsAsync's Task<bool> semantics for an adopted
        // external server, composed from the same pure rules the production
        // reload path runs: the accepted reload is a success...
        Assert.True(ServerLaunch.ReloadSucceeded(
            writeSucceeded: true, serverRunning: true, reloadAccepted: true, recovered: false));
        // ...but its acceptance must not publish or claim actual override
        // application.
        Assert.False(ServerLaunch.ShouldPublishOverrides(
            writeSucceeded: true, reloadAccepted: true, serverUsesModelPresets: false));
    }

    [Theory]
    // A generic unknown-CLI failure is NOT a preset rejection: it stays an
    // ordinary launch failure, for any override state.
    [InlineData("error: unknown argument '--temperture'")]
    [InlineData("error: unrecognized flag '--temperture'")]
    [InlineData("option 'temperture' not recognized")]
    [InlineData("option 'temperture' not recognized in preset")]
    [InlineData("0.02.238.560 E srv start: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 9931")]
    public void GenericCustomCliErrors_NeverEarnTheRetry(string stderr)
    {
        Assert.False(ServerLaunch.TryGetPresetRejection(stderr, out var rejected));
        Assert.False(ServerLaunch.ShouldRetryWithDefaults(
            retryAllowed: true, hasActiveOverrides: true, rejected, retryAlreadyUsed: false));
    }

    [Fact]
    public void ExactPresetRejection_EarnsTheRetry_OnlyWhileOverridesAreActive()
    {
        Assert.True(ServerLaunch.TryGetPresetRejection(
            "option 'temperture' not recognized in preset 'model-context-presets.ini'", out var rejected));

        Assert.True(ServerLaunch.ShouldRetryWithDefaults(
            retryAllowed: true, hasActiveOverrides: true, rejected, retryAlreadyUsed: false));
        Assert.False(ServerLaunch.ShouldRetryWithDefaults(
            retryAllowed: true, hasActiveOverrides: false, rejected, retryAlreadyUsed: false));
    }

    // ---- ReloadModelPresetsAsync result semantics (pure) ----

    [Theory]
    // Running + accepted reload: success.
    [InlineData(true, true, true, false, true)]
    // Stopped = deferred, not failed: the regenerated bytes land on the next
    // start — success even though nothing was reloaded.
    [InlineData(true, false, false, false, true)]
    // Running + rejection and the one-shot recovery landed: success.
    [InlineData(true, true, false, true, true)]
    // Running + rejection and nothing recovered: failure.
    [InlineData(true, true, false, false, false)]
    // A failed preset write leaves the server on stale bytes: failure whether
    // or not a server is running or a request happened to succeed.
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, true, false, false)]
    public void ReloadSucceeded_Matrix(
        bool writeSucceeded, bool serverRunning, bool reloadAccepted, bool recovered, bool expected)
    {
        Assert.Equal(expected, ServerLaunch.ReloadSucceeded(
            writeSucceeded, serverRunning, reloadAccepted, recovered));
    }
}
