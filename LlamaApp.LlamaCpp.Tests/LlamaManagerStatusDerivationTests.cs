using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for <see cref="LlamaManager.DeriveServerStatus"/> — the pure
/// API-poll → server-state mapping that is the app's only source of truth for
/// llama-server status. The rules that matter operationally: a crash is
/// detected identically for spawned and adopted servers (no process handles
/// involved), a reappearing server is adopted from <c>Failed</c> with no
/// relaunch click, and <c>Stopped</c> is never left implicitly.
/// </summary>
public class LlamaManagerStatusDerivationTests
{
    [Theory]
    [InlineData(LlamaManager.ServerState.Running, true, LlamaManager.ServerState.Running)]
    [InlineData(LlamaManager.ServerState.Running, false, LlamaManager.ServerState.Failed)]
    [InlineData(LlamaManager.ServerState.Starting, true, LlamaManager.ServerState.Running)]
    [InlineData(LlamaManager.ServerState.Starting, false, LlamaManager.ServerState.Starting)]
    [InlineData(LlamaManager.ServerState.Failed, true, LlamaManager.ServerState.Running)]
    [InlineData(LlamaManager.ServerState.Failed, false, LlamaManager.ServerState.Failed)]
    [InlineData(LlamaManager.ServerState.Stopped, true, LlamaManager.ServerState.Stopped)]
    [InlineData(LlamaManager.ServerState.Stopped, false, LlamaManager.ServerState.Stopped)]
    public void Derives_Status_From_Api_Poll_Only(
        LlamaManager.ServerState current, bool apiReachable, LlamaManager.ServerState expected)
    {
        Assert.Equal(expected, LlamaManager.DeriveServerStatus(current, apiReachable));
    }

    [Fact]
    public void Crash_And_Recovery_Round_Trip()
    {
        // The crash scenario: a running server dies → Failed; a server
        // reappears (the user restarted their own instance) → Running again —
        // no relaunch click, no process handle.
        var crashed = LlamaManager.DeriveServerStatus(LlamaManager.ServerState.Running, false);
        Assert.Equal(LlamaManager.ServerState.Failed, crashed);

        var recovered = LlamaManager.DeriveServerStatus(crashed, true);
        Assert.Equal(LlamaManager.ServerState.Running, recovered);
    }

    [Fact]
    public void Stopped_Is_Never_Left_Implicitly()
    {
        // Stopped is deliberate app intent (startup before the ensure pipeline
        // runs, the port-reclaim window, app exit): even with a server
        // answering on the port, the supervisor stays out — otherwise it could
        // "adopt" a process StopServer is in the middle of killing. Leaving
        // Stopped is always explicit (ensure / relaunch button).
        Assert.Equal(
            LlamaManager.ServerState.Stopped,
            LlamaManager.DeriveServerStatus(LlamaManager.ServerState.Stopped, true));
    }
}
