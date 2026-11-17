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
/// sequence. Also covers <see cref="OverrideApplyPresentation"/> — the
/// Settings → Llama Model Overrides "Apply" outcome mapping, where an
/// adopted external server must read as deferred configuration, never as
/// overrides applied to a running server.
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
    public void Stopped_While_Installing_Is_Amber_And_Says_So()
    {
        // First-run binary install: hundreds of MB with no other progress
        // indication — the dot must not read a plain gray "stopped".
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing);

        Assert.Equal(0xD2, d.Dot.R); // amber
        Assert.False(d.CanRelaunch);
        Assert.Contains("installing", d.ToolTip);
    }

    [Fact]
    public void Stopped_While_Updating_Existing_Runtime_Does_Not_Say_Installing()
    {
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing,
            failureMessage: null, runtimeAlreadyInstalled: true);

        Assert.Equal(0xD2, d.Dot.R); // still a transient amber state
        Assert.False(d.CanRelaunch);
        Assert.Contains("updating", d.ToolTip);
        Assert.DoesNotContain("installing", d.ToolTip);
        Assert.DoesNotContain("first run", d.ToolTip);
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

    // ----- FailureMessage surfacing ----------------------------------------

    [Fact]
    public void Failed_Without_Message_Keeps_The_Generic_Tooltip()
    {
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle, null);

        Assert.Contains("crashed or failed to start", d.ToolTip);
    }

    [Fact]
    public void Failed_With_Message_Includes_The_Reason()
    {
        // LlamaManager.FailureMessage was set but never read anywhere — the
        // dot's tooltip is where a user asks "why is it red?".
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle,
            "The llama server stopped responding.");

        Assert.Contains("The llama server stopped responding.", d.ToolTip);
        Assert.Contains("relaunch", d.ToolTip);
    }

    [Fact]
    public void Failed_Install_With_Message_Includes_The_Reason()
    {
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Failed,
            "Install script exited with code 1");

        Assert.Contains("install failed: Install script exited with code 1.", d.ToolTip);
        Assert.True(d.CanRelaunch);
    }

    [Fact]
    public void Message_Is_Sanitized_For_A_Tooltip()
    {
        // Multi-line exception text collapses to its first line; a missing
        // trailing period is added so the relaunch hint reads as its own
        // sentence.
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle,
            "first line of the error\nsecond line with a stack trace");

        Assert.Contains("first line of the error.", d.ToolTip);
        Assert.DoesNotContain("second line", d.ToolTip);
    }

    [Fact]
    public void Blank_Message_Falls_Back_To_The_Generic_Tooltip()
    {
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle, "   ");

        Assert.Contains("crashed or failed to start", d.ToolTip);
    }

    // ----- Suspended preset override warning -------------------------------

    [Fact]
    public void Running_With_Suspended_Preset_Is_Amber_And_Says_No_Relaunch()
    {
        // The overrides are suspended but the server IS running (without
        // them): amber, and the tooltip must say the server keeps running as
        // is — a relaunch suggestion here would be actively wrong, since the
        // fix is an edit in Settings → Llama, not a restart.
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle,
            suspendedPresetOption: "contxt-size");

        Assert.Equal(0xD2, d.Dot.R); // amber, not the running green
        Assert.Equal(0x99, d.Dot.G);
        Assert.Equal(0x22, d.Dot.B);
        Assert.False(d.CanRelaunch);
        Assert.Contains("running", d.ToolTip);
        Assert.Contains("contxt-size", d.ToolTip); // names the refused option
        Assert.Contains("models.user.ini", d.ToolTip); // and the file
        Assert.Contains("without a relaunch", d.ToolTip);
    }

    [Fact]
    public void Failed_With_Message_And_Suspended_Preset_Includes_Both()
    {
        // The warning is separate from FailureMessage: both are user-relevant
        // ("why is it red?" vs "why are my overrides missing?") and both land
        // in the tooltip without one replacing the other.
        var d = ServerStatusPresentation.Describe(
            LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle,
            "The llama server stopped responding.",
            suspendedPresetOption: "cache-type-k");

        Assert.Equal(0xF8, d.Dot.R); // still red — the warning is not a state
        Assert.True(d.CanRelaunch);
        Assert.Contains("The llama server stopped responding.", d.ToolTip);
        Assert.Contains("cache-type-k", d.ToolTip);
        Assert.Contains("models.user.ini", d.ToolTip);
        Assert.Contains("relaunch", d.ToolTip);
    }

    [Fact]
    public void No_Warning_Keeps_The_Legacy_Tooltips_Verbatim()
    {
        // Byte-for-byte compatibility: no warning (parameter omitted, null,
        // or blank) must not move a single character of the existing
        // tooltips — the warning is appended only when there is one.
        static string Tip(LlamaManager.ServerState s, LlamaManager.InstallState i,
            string? failure = null, bool installed = false)
            => ServerStatusPresentation.Describe(s, i, failure, installed).ToolTip;

        Assert.Equal("llama server: running",
            Tip(LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle));
        Assert.Equal("llama server: starting…",
            Tip(LlamaManager.ServerState.Starting, LlamaManager.InstallState.Idle));
        Assert.Equal("llama server: not running (crashed or failed to start) — use the relaunch button",
            Tip(LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle));
        Assert.Equal("llama server: not running — The llama server stopped responding. Use the relaunch button.",
            Tip(LlamaManager.ServerState.Failed, LlamaManager.InstallState.Idle,
                "The llama server stopped responding."));
        Assert.Equal("llama server: stopped",
            Tip(LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Idle));
        Assert.Equal("llama server: installing llama.cpp — the first run can take a few minutes…",
            Tip(LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing));
        Assert.Equal("llama server: updating llama.cpp…",
            Tip(LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Installing,
                installed: true));
        Assert.Equal("llama server: stopped (install failed) — use the relaunch button",
            Tip(LlamaManager.ServerState.Stopped, LlamaManager.InstallState.Failed));

        // An explicitly blank warning is the same as none at all.
        Assert.Equal("llama server: running",
            ServerStatusPresentation.Describe(
                LlamaManager.ServerState.Running, LlamaManager.InstallState.Idle,
                suspendedPresetOption: "   ").ToolTip);
    }

    // ----- Model Overrides Apply outcomes ----------------------------------

    [Fact]
    public void Apply_Failed_Reload_Reports_A_Failure()
    {
        var d = OverrideApplyPresentation.Describe(
            reloadSucceeded: false, suspendedOption: null,
            serverRunning: true, serverUsesModelPresets: true);

        Assert.Equal(OverrideApplyOutcome.Failed, d.Outcome);
        Assert.True(d.IsError);
        Assert.Contains("Couldn't apply", d.StatusText);
    }

    [Fact]
    public void Apply_Suspended_Overrides_Names_The_Refused_Option()
    {
        // A suspension is its own truthful outcome even when the reload
        // reported success (the recovery restart recovered it): the overrides
        // are NOT in effect, so "applied" would be a lie.
        var d = OverrideApplyPresentation.Describe(
            reloadSucceeded: true, suspendedOption: "contxt-size",
            serverRunning: true, serverUsesModelPresets: true);

        Assert.Equal(OverrideApplyOutcome.Suspended, d.Outcome);
        Assert.True(d.IsError);
        Assert.Contains("contxt-size", d.StatusText);
        Assert.Contains("suspended", d.StatusText);
    }

    [Fact]
    public void Apply_On_A_Running_App_Managed_Server_Is_Applied()
    {
        // Only an app-managed server (it was launched with --models-preset)
        // actually reads the override file — this is the one branch that may
        // say "applied to the running llama server".
        var d = OverrideApplyPresentation.Describe(
            reloadSucceeded: true, suspendedOption: null,
            serverRunning: true, serverUsesModelPresets: true);

        Assert.Equal(OverrideApplyOutcome.AppliedToRunningServer, d.Outcome);
        Assert.False(d.IsError);
        Assert.Equal("Overrides applied to the running llama server.", d.StatusText);
    }

    [Fact]
    public void Apply_On_An_Applied_External_Server_Is_Deferred_Not_Applied()
    {
        // An adopted external server answers the reload (HTTP 200) but never
        // reads the app's --models-preset file. Reporting "applied to the
        // running llama server" here was the lie: the configuration is saved
        // and deferred to a future app-managed server instead.
        var d = OverrideApplyPresentation.Describe(
            reloadSucceeded: true, suspendedOption: null,
            serverRunning: true, serverUsesModelPresets: false);

        Assert.Equal(OverrideApplyOutcome.DeferredToFutureServer, d.Outcome);
        Assert.False(d.IsError);
        Assert.DoesNotContain("applied to the running", d.StatusText);
        Assert.Contains("deferred", d.StatusText);
        Assert.Contains("app-managed", d.StatusText);
    }

    [Fact]
    public void Apply_With_No_Server_Running_Is_Deferred_To_The_Next_Run()
    {
        var d = OverrideApplyPresentation.Describe(
            reloadSucceeded: true, suspendedOption: null,
            serverRunning: false, serverUsesModelPresets: false);

        Assert.Equal(OverrideApplyOutcome.Deferred, d.Outcome);
        Assert.False(d.IsError);
        Assert.Contains("they'll apply when the llama server next runs", d.StatusText);
    }
}
