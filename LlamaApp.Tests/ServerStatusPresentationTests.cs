using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="ServerStatusPresentation"/> — the pure mapping
/// from the llama server's state to the footer's status dot color, tooltip,
/// and relaunch-button visibility. The relaunch rules matter most: the
/// button must appear when the server is down for good (crash, failed
/// install) but never flash during the startup Stopped → Starting → Running
/// sequence.
/// </summary>
public class ServerStatusPresentationTests
{
    [Fact]
    public void Running_Is_Green_Without_Relaunch()
    {
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle);

        Assert.Equal(0x3F, d.Dot.R);
        Assert.Equal(0xB9, d.Dot.G);
        Assert.Equal(0x50, d.Dot.B);
        Assert.False(d.CanRelaunch);
        Assert.Contains("running", d.ToolTip);
    }

    [Fact]
    public void Starting_Is_Amber_Without_Relaunch()
    {
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Starting, LlamaManager.InstallState.Idle);

        Assert.Equal(0xD2, d.Dot.R);
        Assert.False(d.CanRelaunch);
        Assert.Contains("starting", d.ToolTip);
    }

    [Fact]
    public void Failed_Is_Red_With_Relaunch()
    {
        // The crash case: red dot, and the relaunch button must show — this
        // is the state a server dies into mid-download / mid-load.
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle);

        Assert.Equal(0xF8, d.Dot.R);
        Assert.Equal(0x51, d.Dot.G);
        Assert.Equal(0x49, d.Dot.B);
        Assert.True(d.CanRelaunch);
    }

    [Fact]
    public void Stopped_At_Startup_Hides_Relaunch()
    {
        // Startup transient (Stopped, install state still Idle): the ensure
        // pipeline is about to move to Starting — the button must not flash.
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Idle);

        Assert.False(d.CanRelaunch);
    }

    [Fact]
    public void Stopped_After_Failed_Install_Shows_Relaunch()
    {
        // First-run install failure leaves the server Stopped (never
        // reached Failed) — the button is the only way to retry.
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Failed);

        Assert.True(d.CanRelaunch);
    }
}
