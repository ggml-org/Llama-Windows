using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Regression coverage for the progress-toast lifecycle behind issue #193:
/// ticks update one toast, while dismissal and terminal cleanup prevent a
/// queued callback from displaying another one.
/// </summary>
public class ProgressToastStateTests
{
    [Fact]
    public void First_Report_Shows_Then_Ticks_Update_With_Increasing_Sequences()
    {
        var state = new ProgressToastState();

        Assert.Equal(ProgressToastAction.Show, state.NextAction());
        Assert.Equal(1u, state.NextSequence());
        state.MarkShown();

        Assert.Equal(ProgressToastAction.Update, state.NextAction());
        Assert.Equal(2u, state.NextSequence());
        Assert.Equal(ProgressToastAction.Update, state.NextAction());
    }

    [Fact]
    public void Dismissal_Suppresses_Updates_And_Transient_Reopen_Does_Not_Reset_It()
    {
        var state = new ProgressToastState();
        state.MarkShown();
        state.Suppress(); // AppNotificationNotFound: the user dismissed it.

        Assert.Equal(ProgressToastAction.Ignore, state.NextAction());
        state.Close(terminal: false);
        Assert.Equal(ProgressToastAction.Ignore, state.NextAction());
    }

    [Fact]
    public void Terminal_Close_Suppresses_A_Queued_First_Report()
    {
        var state = new ProgressToastState();
        state.Close(terminal: true);

        Assert.Equal(ProgressToastAction.Ignore, state.NextAction());
    }

    [Fact]
    public void Transient_Close_Allows_A_Later_Show()
    {
        var state = new ProgressToastState();
        state.MarkShown();
        state.Close(terminal: false);

        Assert.Equal(ProgressToastAction.Show, state.NextAction());
    }
}
