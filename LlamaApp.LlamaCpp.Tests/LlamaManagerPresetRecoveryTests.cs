using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Deterministic coverage for the managed-server preset-rejection recovery's
/// line-safe output tail: a fresh revisioned latch per spawned process that
/// outlives the 20-line buffer eviction, and the wait-for-new-revision
/// contract the reload path uses to recognize "a rejection just appeared" —
/// plus the recovery's conditional suspension (only the exact rejected
/// revision is suspended, never a newer corrected file) and its
/// cancellation-before-side-effects rule. Also covers the server-identity
/// epoch publication guard (a retired identity's acceptance is never
/// republished), the <c>--models-preset</c> capability sidecar (persisted only
/// for launches with a non-null preset path; managed adoption needs a live
/// managed PID AND the proof), and the poll loss rule (a transient
/// connectivity failure with a live managed process preserves the pins and
/// epoch; only confirmed process loss invalidates). Actual identity transitions
/// also cover stale retirement, pending-recovery evidence through the refresh
/// entry point, and notifications outside the identity lock. Process objects
/// are unstarted identity tokens; HTTP handlers control responses, so there is
/// no real process, socket, or timing dependence. Rejection waits use already
/// latched revisions or zero budgets. The retry policy matrix lives in
/// <see cref="ServerLaunchTests"/>.
/// </summary>
public sealed class LlamaManagerPresetRecoveryTests : IDisposable
{
    private const string RejectionLine =
        "0.02.238.560 E srv start: option 'temperture' not recognized in preset 'model-context-presets.ini'";

    private readonly string _dir;
    private readonly string _path;

    public LlamaManagerPresetRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "llama-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, UserModelOverrides.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private UserModelOverrides NewOverrides() => new(_path);

    private void WriteUserFile(string content) => File.WriteAllText(_path, content);

    private LlamaManager NewManager(UserModelOverrides overrides)
        => new(2276, "127.0.0.1", null, overrides, _dir, startSupervisor: false);

    private static void ConfigureModelFetch(LlamaManager manager, HttpClient http, long writtenGeneration)
    {
        var httpField = typeof(LlamaManager).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)httpField.GetValue(manager)!).Dispose();
        httpField.SetValue(manager, http);
        typeof(LlamaManager).GetField("_writtenOverridesGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, writtenGeneration);
        SetServerRunning(manager);
    }

    private static void SetServerRunning(LlamaManager manager)
        => typeof(LlamaManager).GetProperty(nameof(LlamaManager.ServerStatus))!
            .SetValue(manager, LlamaManager.ServerState.Running);

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    [Fact]
    public async Task RejectionIsRetained_AfterMoreThanTwentyChatterLines()
    {
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add(RejectionLine);
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");

        // The rejection LINE is long evicted from the 20-line buffer...
        Assert.DoesNotContain("not recognized", tail.Snapshot());
        // ...but the revisioned latch survives it and still answers a
        // wait-for-new-revision with an old baseline (TimeSpan.Zero budget:
        // the latched revision is checked first, so no timing involved).
        Assert.Equal("temperture", tail.LatestRejection!.Value.Option);
        Assert.True(await tail.WaitForNewRejectionAsync(0, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Wait_SeesOnlyRevisionsNewerThanTheBaseline()
    {
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add(RejectionLine);

        // A caller that already saw revision 1 must NOT be handed this same
        // rejection as "new" — an old diagnostic lingering on the tail must
        // never retrigger the recovery.
        Assert.False(await tail.WaitForNewRejectionAsync(1, TimeSpan.Zero, CancellationToken.None));

        // A second rejection arriving later advances the revision and becomes
        // new relative to the same baseline.
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");
        tail.Add("0.03.000.000 E srv start: option 'top-p-override' not recognized in preset 'model-context-presets.ini'");

        Assert.True(await tail.WaitForNewRejectionAsync(1, TimeSpan.Zero, CancellationToken.None));
        Assert.Equal("top-p-override", tail.LatestRejection!.Value.Option);
    }

    [Fact]
    public async Task Wait_TimesOutWithoutAnyNewRejection()
    {
        var tail = new LlamaManager.ServerOutputTail();
        for (var i = 0; i < 5; i++) tail.Add($"chatter line {i}");

        // No rejection at all, zero budget: deterministic false.
        Assert.False(await tail.WaitForNewRejectionAsync(0, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void Revision_AdvancesOncePerLine_AndNeverRewinds()
    {
        var tail = new LlamaManager.ServerOutputTail();
        Assert.Equal(0, tail.Revision);

        tail.Add(RejectionLine);
        Assert.Equal(1, tail.Revision);
        Assert.Equal(1, tail.LatestRejection!.Value.Revision);

        // Eviction rewinds nothing: the baseline a caller captured before the
        // event stays meaningful.
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");
        Assert.Equal(26, tail.Revision);
        Assert.Equal(1, tail.LatestRejection!.Value.Revision);
    }

    [Fact]
    public void RejectionIsLatchedFromItsOwnLine_EvenAmongChatter()
    {
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add("0.00.012.342 I srv llama_server: initializing ...");
        tail.Add("garbage without a diagnostic");
        tail.Add(RejectionLine);
        tail.Add("0.02.239.489 I srv operator(): cleaning up before exit...");

        Assert.Equal("temperture", tail.LatestRejection!.Value.Option);
    }

    [Fact]
    public void TailsArePerProcess_OneTailsLatchNeverLeaksIntoAnother()
    {
        var first = new LlamaManager.ServerOutputTail();
        var second = new LlamaManager.ServerOutputTail();
        first.Add(RejectionLine);

        // "A fresh tail per spawned process": the second spawn must start with
        // a clean latch and revision, regardless of what the first saw.
        Assert.NotNull(first.LatestRejection);
        Assert.Null(second.LatestRejection);
        Assert.Equal(0, second.Revision);
    }

    [Fact]
    public void Snapshot_KeepsTheNewestTwentyLines()
    {
        var tail = new LlamaManager.ServerOutputTail();
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");

        var snapshot = tail.Snapshot();
        Assert.NotNull(snapshot);
        Assert.DoesNotContain("chatter line 4", snapshot);
        Assert.Contains("chatter line 24", snapshot);
    }

    // ---- conditional suspension: only the rejected revision (the exact
    // RecoverFromPresetRejection contract: capture the staged generation at
    // write time, suspend with it — or refuse to) ----

    [Fact]
    public void Suspend_WithTheCapturedGeneration_TakesEffect()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemp = 0.5\n\n");
        overrides.ReloadAndApply("[g]\n");
        var staged = overrides.Generation; // what WriteContextPresetsIniAsync records

        // The rejection is about the revision the server was handed — the
        // suspension lands and the option is remembered for the warning row.
        Assert.True(overrides.Suspend("temperture", staged));
        Assert.Equal("temperture", overrides.SuspendedOption);
    }

    [Fact]
    public void Suspend_IsRefused_WhenTheUserFixedTheFileAfterTheRejection()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\n\n");
        overrides.ReloadAndApply("[g]\n");
        var staged = overrides.Generation;

        // The user fixes the typo before the rejection is processed — the
        // write/reload bumps the generation to the corrected revision.
        WriteUserFile("[m]\ntop-p = 0.8\n\n");
        overrides.ReloadAndApply("[g]\n");

        // The rejection was about the OLD revision: suspending now would
        // freeze a corrected file. The generation mismatch refuses it, and
        // the corrected overrides survive for the relaunch.
        Assert.False(overrides.Suspend("temperture", staged));
        Assert.Null(overrides.SuspendedOption);
        Assert.True(overrides.HasActiveOverrides);
    }

    [Fact]
    public async Task RecoverySequence_LatchThenSuspendTheSameRejectedRevision()
    {
        // The reload recovery's decision sequence against the real pieces:
        // baseline → wait for a NEW rejection → suspend exactly the revision
        // the rejected file carried. The rejection arrives while the wait runs
        // and its line is then evicted — the latch, not the snapshot, answers.
        var tail = new LlamaManager.ServerOutputTail();
        var baseline = tail.Revision;

        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\n\n");
        overrides.ReloadAndApply("[g]\n");
        var staged = overrides.Generation;

        tail.Add(RejectionLine);
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");

        Assert.True(await tail.WaitForNewRejectionAsync(baseline, TimeSpan.Zero, CancellationToken.None));
        Assert.DoesNotContain("not recognized", tail.Snapshot()); // evicted — no snapshot veto

        var rejection = tail.LatestRejection;
        Assert.NotNull(rejection);
        Assert.True(rejection.Value.Revision > baseline);
        Assert.True(overrides.Suspend(rejection.Value.Option, staged));
        Assert.Equal("temperture", overrides.SuspendedOption);
    }

    [Fact]
    public async Task RecoverySequence_IsSkipped_WhenCancellationArrivesFirst()
    {
        // Cancellation outranks the recovery at every step: the wait observes
        // it, and the pre-side-effect guard declines the recovery even when a
        // perfectly good NEW rejection is latched — no suspension may happen
        // for a cancelled attempt.
        var tail = new LlamaManager.ServerOutputTail();
        var baseline = tail.Revision;

        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\n\n");
        overrides.ReloadAndApply("[g]\n");

        // Cancellation before any rejection: the wait itself observes it...
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tail.WaitForNewRejectionAsync(baseline, TimeSpan.FromSeconds(2), cts.Token));

        // ...and the guard (the production pre-suspension check) declines.
        Assert.False(ServerLaunch.ShouldRecoverFromLaunchRejection(
            retryAllowed: true, hasActiveOverrides: true,
            rejectedOption: "temperture", cancellationRequested: true));

        // Even with a NEW latched rejection the wait would say yes to, a
        // cancelled caller is still declined before the suspension side
        // effect — and nothing was suspended.
        tail.Add(RejectionLine);
        Assert.True(await tail.WaitForNewRejectionAsync(baseline, TimeSpan.Zero, CancellationToken.None));
        Assert.False(ServerLaunch.ShouldRecoverFromLaunchRejection(
            retryAllowed: true, hasActiveOverrides: true,
            tail.LatestRejection?.Option, cancellationRequested: cts.Token.IsCancellationRequested));
        Assert.Null(overrides.SuspendedOption);
    }

    [Fact]
    public void PublishOnlyAfterAcceptance_PinnedCtxSizeFollowsTheAcceptedConfiguration()
    {
        // The staging/publish split the backend relies on: ReloadAndApply
        // stages, PublishOverrides (called only after a successful app-managed
        // startup or accepted reload) makes the pinned ctx-size visible, and a
        // suspension clears it immediately.
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 32768\n\n");
        overrides.ReloadAndApply("[g]\n");

        // Staged but not yet accepted: the cache must not claim the value.
        Assert.Null(overrides.OverriddenCtxSize("m"));

        overrides.PublishOverrides();
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));

        Assert.True(overrides.Suspend("temperture", overrides.Generation));
        Assert.Null(overrides.OverriddenCtxSize("m"));
    }

    // ---- override lifecycle across server identity changes: managed vs
    // external adoption, and the recovery/restart republish rule ----

    [Fact]
    public void AdoptionPolicy_OnlyAProvenCapabilityKeepsThePins()
    {
        // The adoption decision rule (AdoptRunningServer's pure core): only an
        // adoption that positively carries the --models-preset capability
        // keeps the pins — an external adoption, or a managed one whose
        // capability is unproven (managed PID alone), drops them. The core
        // invalidation contract these outcomes rely on is covered in
        // UserModelOverridesTests.
        Assert.True(LlamaManager.AdoptionInvalidatesPublishedOverrides(usesModelPresets: false));
        Assert.False(LlamaManager.AdoptionInvalidatesPublishedOverrides(usesModelPresets: true));
    }

    [Fact]
    public void RefreshPublishAfterFailedReload_CommitsTheAcceptedRevisionOnly()
    {
        // A failed reload leaves the staged revision unpublished; the next
        // successful FetchModelsAsync(reload: true) re-reads the preset file
        // and publishes THAT revision — including here, where the user has
        // edited the file in between (generation moved): the guarded publish
        // must commit the revision the accepting transaction wrote, never the
        // newer one that was never accepted.
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 1024\n\n");
        overrides.ReloadAndApply("[g]\n");
        var written = overrides.Generation; // captured around the failed reload's write

        // The reload is refused by the server: nothing is published.
        Assert.Null(overrides.OverriddenCtxSize("m"));

        // The user edits the file while the earlier reload is still failing;
        // the next write/reload stages (and the refresh accepts) the newer
        // revision — the one whose bytes are actually on disk for the server.
        WriteUserFile("[m]\nctx-size = 2048\n\n");
        overrides.ReloadAndApply("[g]\n");
        var accepted = overrides.Generation;
        Assert.NotEqual(written, accepted);

        // The refresh's publish of its own captured generation lands...
        overrides.PublishOverrides(accepted);
        Assert.Equal("2048", overrides.OverriddenCtxSize("m"));

        // ...and a late duplicate publish of the FAILED reload's generation
        // cannot resurrect or roll back to the never-accepted revision.
        overrides.PublishOverrides(written);
        Assert.Equal("2048", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void ExternalAdoptionDropsPins_ButAFreshAcceptanceRepublishes()
    {
        // External adoption invalidates the effective pins (the server runs no
        // --models-preset of ours) while preserving the suspension and the raw
        // config; a later accepted (re)load on a preset-consuming server
        // republishes them — the successful-restart rule.
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 32768\n\n");
        overrides.ReloadAndApply("[g]\n");
        var staged = overrides.Generation;
        overrides.PublishOverrides(staged);
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));

        overrides.InvalidatePublishedOverrides(); // the external-adoption step

        Assert.Null(overrides.OverriddenCtxSize("m")); // pins no server is known to hold
        Assert.Equal(staged, overrides.Generation);   // raw config survives untouched
        Assert.Null(overrides.SuspendedOption);

        // A successful restart/reload on a preset-consuming server accepts the
        // same staged revision and brings the pins right back.
        overrides.PublishOverrides(staged);
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void RecoverySuspensionIsTemporary_FixedFileRepublishesOnTheNextAcceptance()
    {
        // The recovery's suspension preserves the raw config (RecoverFromPresetRejection
        // only suspends), and a successful restart after the user fixes the
        // file republishes the corrected overrides — the end-to-end recovery
        // arc: rejection → suspension (pins dead) → fix → accepted rewrite →
        // publish.
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\nctx-size = 32768\n\n");
        overrides.ReloadAndApply("[g]\n");
        var rejected = overrides.Generation;
        overrides.PublishOverrides(rejected);
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));

        // The rejection is about the revision the server was handed: the
        // recovery suspends exactly it (see RecoverFromPresetRejection), and
        // the pins die with the suspension.
        Assert.True(overrides.Suspend("temperture", rejected));
        Assert.Null(overrides.OverriddenCtxSize("m"));

        // The user fixes the typo; the next preset rewrite reloads the file —
        // the edit clears the suspension, and the restart's accepted config is
        // published again.
        WriteUserFile("[m]\ntop-p = 0.8\nctx-size = 32768\n\n");
        overrides.ReloadAndApply("[g]\n");
        var corrected = overrides.Generation;
        Assert.NotEqual(rejected, corrected);
        Assert.Null(overrides.SuspendedOption);

        overrides.PublishOverrides(corrected);
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    // ---- server-identity epoch: publication is refused once the identity a
    // publication attempt captured has moved (stop/adoption/confirmed loss all
    // advance it — a retired server's acceptance is never republished) ----

    [Fact]
    public void StaleIdentityPublication_IsRefused_AfterTheIdentityMoved()
    {
        // The exact guard PublishOverrides runs under _tailGate (see
        // LlamaManager.PublicationAllowed): the captured epoch must still be
        // current AND the identity must still consume --models-preset. A
        // response that straddled a stop/adoption/retirement captures an epoch
        // that is no longer current — its acceptance belongs to a retired
        // server and must never republish its pins.
        Assert.True(LlamaManager.PublicationAllowed(2, 2, serverUsesModelPresets: true));

        // The identity moved (a new spawn install, a stop, or an adoption all
        // advance the epoch): the late acceptance is stale.
        Assert.False(LlamaManager.PublicationAllowed(2, 3, serverUsesModelPresets: true));

        // The same epoch but no preset capability (an adopted external or
        // unproven server): nothing of ours was applied, so nothing publishes.
        Assert.False(LlamaManager.PublicationAllowed(2, 2, serverUsesModelPresets: false));

        // A genuinely retired identity: both wrong at once.
        Assert.False(LlamaManager.PublicationAllowed(2, 5, serverUsesModelPresets: false));
    }

    [Fact]
    public void StaleIdentityPublication_IsRefused_EvenWithALaterAcceptedRevision()
    {
        // The epoch guard is independent of the generation guard: even the
        // CURRENT staged generation must not be published for a retired
        // identity. The publication attempt's epoch token is what binds the
        // acceptance to the server that gave it.
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var staged = overrides.Generation;

        // Simulates PublishOverrides(staged, capturedEpoch) arriving after a
        // stop retired the identity: refused, pins stay dead.
        Assert.False(LlamaManager.PublicationAllowed(0, 1, serverUsesModelPresets: true));
        Assert.Null(overrides.OverriddenCtxSize("m"));

        // A publication for the CURRENT identity with the same generation is
        // the ordinary accepted (re)load — it lands.
        Assert.True(LlamaManager.PublicationAllowed(1, 1, serverUsesModelPresets: true));
        overrides.PublishOverrides(staged);
        Assert.Equal("4096", overrides.OverriddenCtxSize("m"));
    }

    // ---- positive capability evidence: the sidecar is written only for
    // launches with a non-null --models-preset path, and managed adoption
    // requires a live managed PID AND that proof — never the PID alone ----

    [Fact]
    public void CapabilityEvidence_IsPersisted_OnlyForANonNullPresetPath()
    {
        var capability = Path.Combine(_dir, ".llama.presets");

        // A launch that handed the process --models-preset records the path —
        // the positive proof a later adoption pairs with the managed PID.
        LlamaManager.RecordPresetCapability(capability, @"C:\shared\presets.ini");
        Assert.Equal(@"C:\shared\presets.ini", LlamaManager.ReadPresetCapabilityFile(capability));

        // A launch whose preset write FAILED (null path) runs without the flag:
        // no proof may be recorded — and any proof an earlier launch left
        // behind must not outlive its process.
        LlamaManager.RecordPresetCapability(capability, null);
        Assert.False(File.Exists(capability));
        Assert.Null(LlamaManager.ReadPresetCapabilityFile(capability));
    }

    [Fact]
    public void CapabilityEvidence_MissingOrEmpty_IsNotProof()
    {
        var capability = Path.Combine(_dir, ".llama.presets");

        // No sidecar at all: no proof.
        Assert.Null(LlamaManager.ReadPresetCapabilityFile(capability));

        // An empty sidecar (a crashed write) is not proof either.
        File.WriteAllText(capability, "   \n");
        Assert.Null(LlamaManager.ReadPresetCapabilityFile(capability));
    }

    [Fact]
    public void ManagedAdoption_RequiresLivePidAndCapabilityProof()
    {
        var capability = Path.Combine(_dir, ".llama.presets");

        // Managed PID + sidecar: the only combination that carries the
        // --models-preset capability (and therefore the pins) into the
        // adopted identity.
        LlamaManager.RecordPresetCapability(capability, @"C:\shared\presets.ini");
        Assert.True(LlamaManager.TryAdoptPresetCapability(true, capability));

        // Managed PID WITHOUT the sidecar (legacy instance, or one whose
        // preset write failed): unknown capability — conservative, no claim,
        // and the stale sidecar is cleaned up if any lingers.
        LlamaManager.DeletePresetCapabilityFile(capability);
        Assert.False(LlamaManager.TryAdoptPresetCapability(true, capability));
        Assert.False(File.Exists(capability));

        // No live managed PID (external adoption) with a leftover sidecar
        // from a dead managed server: never proof, and the stale evidence is
        // removed.
        LlamaManager.RecordPresetCapability(capability, @"C:\shared\presets.ini");
        Assert.False(LlamaManager.TryAdoptPresetCapability(false, capability));
        Assert.False(File.Exists(capability));
    }

    [Fact]
    public void AdoptionUsesModelPresets_ManagedWithoutEvidence_IsConservative()
    {
        // Managed + proven is the only preset-capable adoption. A managed PID
        // alone is unknown legacy capability — never inferred to carry it.
        Assert.True(LlamaManager.AdoptionUsesModelPresets(true, true));
        Assert.False(LlamaManager.AdoptionUsesModelPresets(true, false));
        Assert.False(LlamaManager.AdoptionUsesModelPresets(false, true));
        Assert.False(LlamaManager.AdoptionUsesModelPresets(false, false));
    }

    // ---- poll failures: a transient /models+/health failure with a live
    // managed process preserves the pins and the identity epoch; only a
    // confirmed process loss invalidates ----

    [Fact]
    public void TransientFailure_LiveProcess_PreservesPinsAndEpoch()
    {
        // A temporary connectivity failure while the identity's managed
        // process is still live changes NOTHING: the pins stay published and
        // the epoch is untouched, so no poll republishes what never died.
        Assert.False(LlamaManager.PollFailureRetiresIdentity(true));
    }

    [Fact]
    public void TransientFailure_PreserveRule_AppliesToEveryFailingTick()
    {
        // The loss rule is re-evaluated on every failing tick (ApplyPolledStatus
        // runs it before the transition check), so a failure that stays
        // transient — the process is alive tick after tick — keeps retiring
        // nothing. Deterministic: the predicate is a pure function of the
        // observed liveness.
        for (var tick = 0; tick < 5; tick++)
            Assert.False(LlamaManager.PollFailureRetiresIdentity(true));
    }

    [Fact]
    public void ConfirmedProcessLoss_Invalidates()
    {
        // The identity's managed process is gone — the accepted pins describe
        // a server that no longer exists. Confirmed loss retires the identity
        // (epoch advances, pins die with it).
        Assert.True(LlamaManager.PollFailureRetiresIdentity(false));
    }

    [Fact]
    public void UnknownProcessState_IsNeverConfirmedLoss()
    {
        // Nothing of ours to observe (an external adoption) or a process check
        // that could not run: liveness is unknown, never a confirmation — the
        // identity (and its pins) survive, and the re-adoption on recovery is
        // what settles a replaced server.
        Assert.False(LlamaManager.PollFailureRetiresIdentity(null));
    }

    [Fact]
    public void RecoveryAdoption_FollowsTheSameLivenessRule()
    {
        // Failed → Running: a verifiably live identity process means the SAME
        // server is back after a transient failure — its identity, pins and
        // epoch survive untouched, no adoption (and no republish). Anything
        // else (confirmed loss, an external identity, an unverifiable process)
        // adopts the answering server through the capability rule.
        Assert.False(LlamaManager.PollRecoveryAdoptsServer(true));
        Assert.True(LlamaManager.PollRecoveryAdoptsServer(false));
        Assert.True(LlamaManager.PollRecoveryAdoptsServer(null));
    }

    [Fact]
    public void StaleRetirementAndAdoption_CannotEraseReplacementPinsOrCapability()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var manager = NewManager(overrides);
        using var oldProcess = new Process();
        using var replacement = new Process();
        var oldRevision = manager.SetServerTailIdentity(
            oldProcess, new LlamaManager.ServerOutputTail(), "old-presets.ini", 101);
        manager.PublishOverrides(overrides.Generation, oldRevision);

        // The supervisor checked the old identity, but a new spawn installed
        // its own accepted configuration before that check could retire it.
        var replacementTail = new LlamaManager.ServerOutputTail();
        var replacementRevision = manager.SetServerTailIdentity(
            replacement, replacementTail, "replacement-presets.ini", 102);
        Assert.Null(overrides.OverriddenCtxSize("m"));
        manager.PublishOverrides(overrides.Generation, replacementRevision);

        Assert.False(manager.RetireServerIdentity(oldRevision));
        Assert.False(manager.AdoptRunningServerFromEvidence(oldRevision, managedPid: 0));
        Assert.Equal("4096", overrides.OverriddenCtxSize("m"));
        Assert.Equal("replacement-presets.ini",
            LlamaManager.ReadPresetCapabilityFile(Path.Combine(_dir, ".llama.presets")));
        Assert.Equal("102", File.ReadAllText(Path.Combine(_dir, ".llama.pid")));
        var current = manager.BeginPresetReload();
        Assert.Equal(replacementRevision, current.Revision);
        Assert.Same(replacementTail, current.Tail);
        Assert.True(current.UsesPresets);
        manager.EndPresetReload(current.Revision);
    }

    [Fact]
    public async Task PendingRecovery_PreservesDeadSpawnEvidence_AndClaimsExactlyOneReplacement()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var rejectedGeneration = overrides.Generation;
        var manager = NewManager(overrides);
        using var rejectedProcess = new Process();
        using var replacement = new Process();
        var rejectedTail = new LlamaManager.ServerOutputTail();
        var rejectedRevision = manager.SetServerTailIdentity(
            rejectedProcess, rejectedTail, "rejected-presets.ini", 101);
        manager.PublishOverrides(rejectedGeneration, rejectedRevision);
        var firstReload = manager.BeginPresetReload();
        var secondReload = manager.BeginPresetReload();

        // HTTP failed and the supervisor confirmed death before the drain
        // delivered the diagnostic. Neither loss nor adoption may erase it.
        Assert.False(manager.RetireServerIdentity(rejectedRevision));
        Assert.False(manager.AdoptRunningServerFromEvidence(rejectedRevision, managedPid: 0));
        Assert.Equal("4096", overrides.OverriddenCtxSize("m"));
        Assert.Equal("rejected-presets.ini",
            LlamaManager.ReadPresetCapabilityFile(Path.Combine(_dir, ".llama.presets")));
        rejectedTail.Add(RejectionLine);
        for (var i = 0; i < 30; i++) rejectedTail.Add($"chatter {i}");
        Assert.Same(rejectedTail, firstReload.Tail);
        Assert.True(await firstReload.Tail!.WaitForNewRejectionAsync(
            firstReload.Baseline, TimeSpan.Zero, CancellationToken.None));

        // The actual recovery transition still owns the dead process and
        // latched tail. A second recovery cannot claim that process again.
        Assert.Same(rejectedProcess, manager.PreparePresetRecovery(
            rejectedTail.LatestRejection!.Value.Option, rejectedTail, rejectedGeneration));
        Assert.Null(manager.PreparePresetRecovery("temperture", rejectedTail, rejectedGeneration));
        Assert.Equal("temperture", overrides.SuspendedOption);
        Assert.Null(overrides.OverriddenCtxSize("m"));
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.presets")));
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.pid")));

        WriteUserFile("[m]\nctx-size = 8192\n\n");
        overrides.ReloadAndApply("[g]\n");
        var replacementTail = new LlamaManager.ServerOutputTail();
        var replacementRevision = manager.SetServerTailIdentity(
            replacement, replacementTail, "replacement-presets.ini", 102);
        manager.PublishOverrides(overrides.Generation, replacementRevision);

        // Even a pending loss on the NEW epoch belongs to its own reload.
        // Finally blocks from both old observers must neither decrement its
        // counter nor execute their old deferred retirement against it.
        var replacementReload = manager.BeginPresetReload();
        Assert.False(manager.RetireServerIdentity(replacementRevision));
        manager.EndPresetReload(firstReload.Revision);
        manager.EndPresetReload(secondReload.Revision);
        Assert.Null(manager.PreparePresetRecovery("temperture", rejectedTail, rejectedGeneration));
        Assert.Same(replacementTail, replacementReload.Tail);
        Assert.Equal(rejectedRevision + 2, replacementReload.Revision);
        Assert.Equal("8192", overrides.OverriddenCtxSize("m"));
        Assert.Null(overrides.SuspendedOption);
        Assert.True(manager.ServerUsesModelPresets);
        Assert.Equal("replacement-presets.ini",
            LlamaManager.ReadPresetCapabilityFile(Path.Combine(_dir, ".llama.presets")));

        manager.EndPresetReload(replacementReload.Revision);
        Assert.False(manager.ServerUsesModelPresets);
        Assert.Null(overrides.OverriddenCtxSize("m"));
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.presets")));
    }

    [Fact]
    public async Task RefreshModels_FatalRejection_RetainsEvidenceThroughRecoveryWithoutRetiringReplacement()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var manager = NewManager(overrides);
        using var rejectedProcess = new Process();
        using var replacement = new Process();
        var rejectedTail = new LlamaManager.ServerOutputTail();
        var rejectedRevision = manager.SetServerTailIdentity(
            rejectedProcess, rejectedTail, "rejected-presets.ini", 101);
        manager.PublishOverrides(overrides.Generation, rejectedRevision);
        var capabilityPath = Path.Combine(_dir, ".llama.presets");
        var pidPath = Path.Combine(_dir, ".llama.pid");
        var retiredBeforeDiagnostic = false;
        string? requestPath = null;
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            requestPath = request.RequestUri!.PathAndQuery;
            // Death is observed before the drain delivers the diagnostic.
            retiredBeforeDiagnostic = manager.RetireServerIdentity(rejectedRevision);
            rejectedTail.Add(RejectionLine);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        })) { BaseAddress = new Uri("http://127.0.0.1:2276") };
        ConfigureModelFetch(manager, http, overrides.Generation);

        var recoveryObserved = false;
        string? suspendedOption = null;
        var replacementRevision = 0L;
        var replacementRetired = false;
        manager.StateChanged += (_, _) =>
        {
            if (recoveryObserved || manager.ServerStatus != LlamaManager.ServerState.Stopped) return;
            recoveryObserved = true;
            suspendedOption = overrides.SuspendedOption;
            WriteUserFile("[m]\nctx-size = 8192\n\n");
            overrides.ReloadAndApply("[g]\n");
            replacementRevision = manager.SetServerTailIdentity(
                replacement, new LlamaManager.ServerOutputTail(), "replacement-presets.ini", 102);
            manager.PublishOverrides(overrides.Generation, replacementRevision);
            // A replacement's own observer owns its deferred retirement.
            // The refresh's old finally must not consume that ownership.
            manager.BeginPresetReload();
            replacementRetired = manager.RetireServerIdentity(replacementRevision);
            SetServerRunning(manager);
        };

        var ensureGate = (SemaphoreSlim)typeof(LlamaManager)
            .GetField("_ensureGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        await ensureGate.WaitAsync();
        var refresh = manager.RefreshModelsAsync();
        try
        {
            // The failed request has entered recovery, which is waiting for
            // the restart gate. Retention must extend past the HTTP catch.
            Assert.False(refresh.IsCompleted);
            Assert.Equal("/models?reload=1", requestPath);
            Assert.False(retiredBeforeDiagnostic);
            Assert.False(recoveryObserved);
            Assert.True(manager.ServerUsesModelPresets);
            Assert.Equal("4096", overrides.OverriddenCtxSize("m"));
            Assert.Equal("rejected-presets.ini", LlamaManager.ReadPresetCapabilityFile(capabilityPath));
            Assert.Equal("101", File.ReadAllText(pidPath));
        }
        finally
        {
            ensureGate.Release();
            await refresh;
        }

        Assert.Empty(await refresh);
        Assert.True(recoveryObserved);
        Assert.Equal("temperture", suspendedOption);
        Assert.Equal(rejectedRevision + 2, replacementRevision);
        Assert.False(replacementRetired);
        Assert.True(manager.ServerUsesModelPresets);
        Assert.Equal("8192", overrides.OverriddenCtxSize("m"));
        Assert.Equal("replacement-presets.ini", LlamaManager.ReadPresetCapabilityFile(capabilityPath));
        Assert.Equal("102", File.ReadAllText(pidPath));

        manager.EndPresetReload(replacementRevision);
        Assert.False(manager.ServerUsesModelPresets);
        Assert.Null(overrides.OverriddenCtxSize("m"));
        Assert.False(File.Exists(capabilityPath));
        Assert.False(File.Exists(pidPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FetchModels_OnlyReloadDefersRetirement_AndReleasesAfterTheRequest(bool reload)
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var manager = NewManager(overrides);
        using var process = new Process();
        var revision = manager.SetServerTailIdentity(
            process, new LlamaManager.ServerOutputTail(), "presets.ini", 101);
        manager.PublishOverrides(overrides.Generation, revision);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? requestPath = null;
        using var http = new HttpClient(new DelegateHandler((request, cancel) =>
        {
            requestPath = request.RequestUri!.PathAndQuery;
            return response.Task.WaitAsync(cancel);
        })) { BaseAddress = new Uri("http://127.0.0.1:2276") };
        ConfigureModelFetch(manager, http, overrides.Generation);

        var fetch = reload ? manager.RefreshModelsAsync() : manager.GetModelsAsync();
        try
        {
            Assert.Equal(reload ? "/models?reload=1" : "/models", requestPath);
            Assert.Equal(!reload, manager.RetireServerIdentity(revision));
            Assert.Equal(reload, manager.ServerUsesModelPresets);
            Assert.Equal(reload ? "4096" : null, overrides.OverriddenCtxSize("m"));
            Assert.Equal(reload, File.Exists(Path.Combine(_dir, ".llama.presets")));
        }
        finally
        {
            response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}")
            });
            await fetch;
        }

        Assert.Empty(await fetch);
        Assert.False(manager.ServerUsesModelPresets);
        Assert.Null(overrides.OverriddenCtxSize("m"));
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.presets")));
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.pid")));
    }

    [Fact]
    public void PendingRecovery_WithoutRejection_RetiresAfterTheLastObserverFinishes()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var generation = overrides.Generation;
        var manager = NewManager(overrides);
        using var process = new Process();
        var tail = new LlamaManager.ServerOutputTail();
        var revision = manager.SetServerTailIdentity(process, tail, "presets.ini", 101);
        manager.PublishOverrides(generation, revision);
        var first = manager.BeginPresetReload();
        var second = manager.BeginPresetReload();

        Assert.False(manager.RetireServerIdentity(revision));
        manager.EndPresetReload(first.Revision);
        Assert.True(manager.ServerUsesModelPresets);
        Assert.Equal("4096", overrides.OverriddenCtxSize("m"));
        Assert.True(File.Exists(Path.Combine(_dir, ".llama.presets")));

        // Covers timeout/cancellation exits too: the outer finally is the
        // owner of this release, whether recovery ran or not.
        manager.EndPresetReload(second.Revision);
        manager.PublishOverrides(generation, revision);
        Assert.False(manager.ServerUsesModelPresets);
        Assert.Null(overrides.OverriddenCtxSize("m"));
        Assert.Null(overrides.SuspendedOption);
        Assert.Equal(generation, overrides.Generation);
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.presets")));
        var retired = manager.BeginPresetReload();
        Assert.Equal(revision + 1, retired.Revision);
        Assert.Null(retired.Tail);
        manager.EndPresetReload(retired.Revision);
    }

    [Fact]
    public void PendingRecovery_UserEditPreventsSuspendingTheCorrectedGeneration()
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var rejectedGeneration = overrides.Generation;
        var manager = NewManager(overrides);
        using var process = new Process();
        var tail = new LlamaManager.ServerOutputTail();
        var revision = manager.SetServerTailIdentity(process, tail, "presets.ini", 101);
        var reload = manager.BeginPresetReload();
        tail.Add(RejectionLine);
        Assert.False(manager.RetireServerIdentity(revision));

        WriteUserFile("[m]\nctx-size = 8192\n\n");
        overrides.ReloadAndApply("[g]\n");
        Assert.NotEqual(rejectedGeneration, overrides.Generation);
        Assert.Null(manager.PreparePresetRecovery("temperture", tail, rejectedGeneration));
        Assert.Null(overrides.SuspendedOption);
        Assert.True(manager.ServerUsesModelPresets);

        manager.EndPresetReload(reload.Revision);
        Assert.False(manager.ServerUsesModelPresets);
        Assert.Null(overrides.SuspendedOption);
        Assert.False(File.Exists(Path.Combine(_dir, ".llama.presets")));
    }

    [Theory]
    [InlineData("retirement")]
    [InlineData("adoption")]
    [InlineData("recovery")]
    public void IdentityTransitions_NotifyAfterUnlockAndCleanup(string transition)
    {
        var overrides = NewOverrides();
        WriteUserFile("[m]\ntemperture = 0.5\nctx-size = 4096\n\n");
        overrides.ReloadAndApply("[g]\n");
        var manager = NewManager(overrides);
        using var process = new Process();
        using var replacement = new Process();
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add(RejectionLine);
        var revision = manager.SetServerTailIdentity(process, tail, "old-presets.ini", 101);
        manager.PublishOverrides(overrides.Generation, revision);
        var capabilityPath = Path.Combine(_dir, ".llama.presets");
        var gate = typeof(LlamaManager).GetField("_tailGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        var notified = false;
        var outsideLock = false;
        var cleanedBeforeNotification = false;
        manager.StateChanged += (_, _) =>
        {
            if (notified) return;
            notified = true;
            outsideLock = !Monitor.IsEntered(gate);
            cleanedBeforeNotification =
                !File.Exists(capabilityPath) && overrides.OverriddenCtxSize("m") is null;
            // UI callbacks may re-enter the manager. No trailing cleanup from
            // the transition may erase a replacement installed here.
            manager.SetServerTailIdentity(
                replacement, new LlamaManager.ServerOutputTail(), "replacement-presets.ini", 102);
        };

        switch (transition)
        {
            case "retirement":
                Assert.True(manager.RetireServerIdentity(revision));
                break;
            case "adoption":
                Assert.True(manager.AdoptRunningServerFromEvidence(revision, managedPid: 0));
                break;
            case "recovery":
                Assert.Same(process, manager.PreparePresetRecovery("temperture", tail, overrides.Generation));
                break;
        }

        Assert.True(notified);
        Assert.True(outsideLock);
        Assert.True(cleanedBeforeNotification);
        Assert.True(manager.ServerUsesModelPresets);
        Assert.Equal("replacement-presets.ini", LlamaManager.ReadPresetCapabilityFile(capabilityPath));
        Assert.Equal("102", File.ReadAllText(Path.Combine(_dir, ".llama.pid")));
    }
}
