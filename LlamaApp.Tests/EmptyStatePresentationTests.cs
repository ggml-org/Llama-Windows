using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="EmptyStatePresentation"/> — the Available
/// section's empty-state text must tell the truth in every server/install
/// state (a crashed server or failed install must not read "Starting the
/// llama server…" forever).
/// </summary>
public class EmptyStatePresentationTests
{
    [Fact]
    public void Running_Points_At_Browse_Section()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle,
            runtimeAlreadyInstalled: true, initialModelLoadCompleted: true);

        Assert.Contains("No models installed yet", text);
        Assert.Contains("below", text);
    }

    [Fact]
    public void Starting_Says_Starting()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Starting, LlamaManager.InstallState.Idle,
            runtimeAlreadyInstalled: true, initialModelLoadCompleted: false);

        Assert.Contains("Starting", text);
    }

    [Fact]
    public void Failed_Server_Does_Not_Claim_Starting()
    {
        // Regression: a crashed server used to show "Starting the llama
        // server…" forever.
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle,
            runtimeAlreadyInstalled: true, initialModelLoadCompleted: false);

        Assert.DoesNotContain("Starting", text);
        Assert.Contains("isn't running", text);
    }

    [Fact]
    public void Failed_Install_Does_Not_Claim_Starting()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Failed,
            runtimeAlreadyInstalled: false, initialModelLoadCompleted: false);

        Assert.DoesNotContain("Starting", text);
        Assert.Contains("Couldn't install", text);
    }

    [Fact]
    public void Installing_Explains_The_First_Run_Wait()
    {
        // The binary install can pull hundreds of MB — the empty state is the
        // only place that can say so.
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing,
            runtimeAlreadyInstalled: false, initialModelLoadCompleted: false);

        Assert.Contains("Installing", text);
    }

    [Fact]
    public void Stopped_And_Idle_Is_The_Startup_Moment()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Idle,
            runtimeAlreadyInstalled: true, initialModelLoadCompleted: false);

        Assert.Contains("Starting", text);
    }

    [Fact]
    public void Installing_With_An_Existing_Runtime_Says_Updating()
    {
        // The weekly updater shares InstallState.Installing with first install.
        // On later launches the binary is already resolved, so don't claim a
        // first-time installation is happening again.
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing,
            runtimeAlreadyInstalled: true, initialModelLoadCompleted: false);

        Assert.Contains("Updating", text);
        Assert.DoesNotContain("Installing", text);
        Assert.DoesNotContain("first run", text);
    }

    [Fact]
    public void Running_Before_Initial_Model_Load_Does_Not_Flash_Empty()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle,
            runtimeAlreadyInstalled: true, initialModelLoadCompleted: false);

        Assert.Contains("Loading", text);
        Assert.DoesNotContain("No models installed", text);
    }
}
