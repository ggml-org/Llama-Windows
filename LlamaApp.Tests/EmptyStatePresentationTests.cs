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
    public void Running_Points_At_Recommended_Section()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle);

        Assert.Contains("No models yet", text);
    }

    [Fact]
    public void Starting_Says_Starting()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Starting, LlamaManager.InstallState.Idle);

        Assert.Contains("Starting", text);
    }

    [Fact]
    public void Failed_Server_Does_Not_Claim_Starting()
    {
        // Regression: a crashed server used to show "Starting the llama
        // server…" forever.
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle);

        Assert.DoesNotContain("Starting", text);
        Assert.Contains("isn't running", text);
    }

    [Fact]
    public void Failed_Install_Does_Not_Claim_Starting()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Failed);

        Assert.DoesNotContain("Starting", text);
        Assert.Contains("Couldn't install", text);
    }

    [Fact]
    public void Installing_Explains_The_First_Run_Wait()
    {
        // The binary install can pull hundreds of MB — the empty state is the
        // only place that can say so.
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing);

        Assert.Contains("Installing", text);
    }

    [Fact]
    public void Stopped_And_Idle_Is_The_Startup_Moment()
    {
        var text = EmptyStatePresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Idle);

        Assert.Contains("Starting", text);
    }
}
