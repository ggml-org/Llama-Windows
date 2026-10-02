using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="RuntimeUpdateMessages.Describe"/>: every check
/// outcome the Settings card can receive maps to a non-empty, actionable
/// status line — and the impossible one (NotDue, on a manual check) maps to
/// silence. The wording itself is free to change; the contract is that no
/// real outcome leaves the user without an explanation.
/// </summary>
public class RuntimeUpdateMessagesTests
{
    public static TheoryData<RuntimeUpdateScheduler.CheckOutcome> RealOutcomes => new()
    {
        RuntimeUpdateScheduler.CheckOutcome.SkippedNotManaged,
        RuntimeUpdateScheduler.CheckOutcome.DeferredServerBusy,
        RuntimeUpdateScheduler.CheckOutcome.FetchFailed,
        RuntimeUpdateScheduler.CheckOutcome.SkippedUnparsableVersion,
        RuntimeUpdateScheduler.CheckOutcome.UpToDate,
        RuntimeUpdateScheduler.CheckOutcome.Installed,
        RuntimeUpdateScheduler.CheckOutcome.InstallFailed,
    };

    [Theory]
    [MemberData(nameof(RealOutcomes))]
    public void EveryRealOutcomeExplainsItself(RuntimeUpdateScheduler.CheckOutcome outcome)
    {
        var text = RuntimeUpdateMessages.Describe(outcome);

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void ManualChecksAreNeverNotDue_SoItMapsToSilence()
    {
        // CheckNow bypasses the weekly gate, so a manual check can't be
        // NotDue — the card keeps whatever it showed before.
        Assert.Equal("", RuntimeUpdateMessages.Describe(RuntimeUpdateScheduler.CheckOutcome.NotDue));
    }
}
