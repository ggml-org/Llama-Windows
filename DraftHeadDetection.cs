using LlamaApp.Common;
using LlamaApp.HuggingFace;

namespace LlamaApp;

/// <summary>
/// Detects the speculative-decoding draft heads models ship and wires them
/// into the llama server (issue #97, matching the Mac app).
///
/// <para><b>Why it matters:</b> a small draft head guesses several tokens
/// ahead and the main model verifies them in a single pass — generation gets
/// up to ~2x faster with identical output. Repos like
/// <c>ggml-org/Qwen3.8-27B-GGUF</c> ship <c>mtp-</c> and <c>dflash-</c>
/// sidecar GGUFs, but the server's on-demand download path only fetches the
/// main quant: draft sidecars are downloaded only when the child is given a
/// matching <c>--spec-type</c> (verified against llama.cpp's
/// <c>arg.cpp</c>/<c>download.cpp</c>). The app therefore detects the heads
/// itself — from the Hub repo's file listing — and records the matching
/// <c>spec-type</c> per model in <c>Settings.Current.ModelSpeculativeTypes</c>,
/// which the <c>--models-preset</c> INI renders for the router. At the
/// model's next load the child fetches the sidecar and drafts with it.</para>
///
/// <para><b>When it runs:</b> continuously — <see cref="StartWatching"/>
/// subscribes to the server's models poller, so any model that appears in
/// the server's list and hasn't been checked yet is detected within a poll
/// cycle. That covers app-initiated downloads (checked again below), models
/// downloaded from the WebUI in a browser, models cached before the feature
/// existed, and models adopted from an already-running server — no restart
/// needed. At an app-initiated download, the Hub query runs in parallel
/// with the download and the result is applied only on success (a spec entry
/// for a model that isn't in the HF cache would list a ghost model the
/// router can't load). Empty-string entries are a negative cache ("checked,
/// none shipped") so the Hub is not re-queried; a re-download re-checks.
/// Sidecars beside imported local models need no help: the server's
/// <c>--models-dir</c> scan wires them itself (<c>LLAMA_ARG_SPEC_DRAFT_MODEL</c>
/// in <c>preset.cpp:load_from_models_dir</c>).</para>
///
/// <para>A head detected for a model that is <i>already loaded</i> missed
/// its load — the user is offered a one-click reload (unload + load) that
/// engages it immediately, instead of silently waiting for the next
/// load.</para>
///
/// <para>All failures are best-effort and logged: detection must never block
/// or fault the poller, a download, a load, or startup.</para>
/// </summary>
internal static class DraftHeadDetection
{
    /// <summary>Serializes Hub queries so a burst of unknown models (an
    /// adopted server with a large cache) doesn't stampede the Hub.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Model ids with a detection in flight (avoid duplicate
    /// queries when the poller re-fires mid-detection).</summary>
    private static readonly HashSet<string> InFlight = [];

    /// <summary>Ids whose last detection FAILED (no answer recorded) and
    /// when — the watcher backs off instead of re-querying the Hub on every
    /// poll tick while it's unreachable. A success clears the entry.</summary>
    private static readonly Dictionary<string, DateTimeOffset> FailedAt = [];
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly Lock InFlightLock = new();

    private static bool _watching;

    /// <summary>
    /// Subscribes to the server's models poller: every published snapshot
    /// (change-gated: the event fires when the model list or a load state
    /// changes, plus a slow heartbeat) that carries
    /// a not-yet-checked model id (an owner/repo shape with no entry in the
    /// settings — including the empty negative cache) triggers one detection
    /// for it. Safe to call once per app session; later calls are no-ops.
    /// The poller fires from a background thread, so the handler must never
    /// touch UI state directly (the nudge toast is post-safe).
    /// </summary>
    public static void StartWatching()
    {
        if (_watching) return;
        _watching = true;
        Llama.LlamaManager.Shared.ModelsChanged += OnModelsChanged;
    }

    private static async void OnModelsChanged(
        object? sender, IReadOnlyList<Llama.LlamaManager.ServerModel> snapshot)
    {
        try
        {
            var unknown = new List<(string Id, bool IsLoaded)>();
            lock (InFlightLock)
            {
                foreach (var model in snapshot)
                {
                    if (!model.Id.Contains('/')) continue;       // a models-dir import: sidecars wire themselves
                    if (model.IsDownloading) continue;            // owned by the download path — it persists the entry only on success (a canceled download must leave no ghost INI section)
                    // Checked under the dictionary's own lock: the shared
                    // settings dictionary is mutated from the UI import/delete
                    // paths as well as Apply, and a read racing a write is
                    // undefined for Dictionary (see Apply's lock note).
                    bool alreadyKnown;
                    var specTypes = Settings.Current.ModelSpeculativeTypes;
                    lock (specTypes) alreadyKnown = specTypes.ContainsKey(model.Id);
                    if (alreadyKnown) continue;
                    if (InFlight.Contains(model.Id)) continue;
                    if (FailedAt.TryGetValue(model.Id, out var failedAt) &&
                        DateTimeOffset.UtcNow - failedAt < FailureBackoff) continue;
                    InFlight.Add(model.Id);
                    unknown.Add((model.Id, model.IsLoaded));
                }
            }
            if (unknown.Count == 0) return;

            foreach (var (id, _) in unknown)
            {
                try
                {
                    var specType = await DetectAsync(id);
                    lock (InFlightLock)
                    {
                        if (specType is null)
                            FailedAt[id] = DateTimeOffset.UtcNow; // retry after the backoff, not the next tick
                        else
                            FailedAt.Remove(id);
                    }

                    // Apply (persist + preset reload) even when the detection
                    // predates the model's download completing: the model IS
                    // in the server's list by now (the poller said so), so
                    // the INI entry can't be a ghost.
                    if (specType is not null && Apply(id, specType))
                        await Llama.LlamaManager.Shared.ReloadModelPresetsAsync();

                    // A model that is loaded — or still LOADING, its child
                    // already spawned without the spec-type — missed the
                    // head; offer the one-click reload. Re-read the freshest
                    // snapshot: the detection (a serial Hub query) may have
                    // taken seconds and the enqueued status is stale.
                    if (specType is { Length: > 0 } &&
                        Llama.LlamaManager.Shared.LastModelsSnapshot
                            .FirstOrDefault(m => m.Id == id) is { } current &&
                        (current.IsLoaded || current.IsLoading))
                        NotifyLoadedMissed(id);
                }
                finally
                {
                    lock (InFlightLock) InFlight.Remove(id);
                }
            }
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, "draft-head watcher iteration failed");
        }
    }

    /// <summary>
    /// The one-click reload a "draft head found on a loaded model" nudge
    /// offers (and that the toast activation routes back to): unloads the
    /// model and loads it again, so the child respawns with the recorded
    /// spec-type and the sidecar. A generation in flight is served to
    /// completion by the server's graceful unload; a reload that fails (the
    /// server went down mid-way) is logged — the head still engages at the
    /// model's next ordinary load.
    /// </summary>
    public static async Task ReloadForDraftHeadsAsync(string modelId)
    {
        try
        {
            var mgr = Llama.LlamaManager.Shared;
            var item = new Views.ModelItem { Name = modelId, RepoName = modelId };
            Common.Log.Info($"draft heads: reloading '{modelId}' to engage its draft head");
            await mgr.UnloadModelAsync(item);

            // The router's unload is asynchronous — the child (and its status)
            // take a moment to go down, and a load issued too soon is rejected
            // with "model is already running" (verified live). Wait for the
            // poller to see the model neither loaded nor loading, bounded —
            // on a timeout the head still engages at the next ordinary load.
            for (var i = 0; i < 60; i++)
            {
                var current = mgr.LastModelsSnapshot.FirstOrDefault(m => m.Id == modelId);
                if (current is null || !(current.IsLoaded || current.IsLoading))
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            if (!await mgr.LoadModelAsync(item))
                Common.Log.Warn($"draft heads: reload of '{modelId}' was rejected — the head engages at the model's next load");
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, $"draft-head reload failed for '{modelId}'");
        }
    }

    /// <summary>
    /// Queries the repo's file listing and classifies its draft heads.
    /// Returns the spec-type token (<c>draft-dflash</c>, …), or the empty
    /// string when the repo ships none (the negative cache), or
    /// <c>null</c> when the listing failed (nothing is recorded — the next
    /// poller pass or session retries). Does NOT record anything — callers
    /// combine it with an apply decision, so a detection that races a failed
    /// or canceled download is never persisted.
    /// </summary>
    public static async Task<string?> DetectAsync(string modelId)
    {
        await Gate.WaitAsync();
        try
        {
            var repo = Common.DraftHeads.RepoFromModelId(modelId);
            var client = new HubClient(Settings.Current.HuggingFaceToken);
            var files = await client.ListRepoFilesAsync(repo);
            if (files.Count == 0)
                return null; // missing/gated repo or network failure — retry later

            var kind = Common.DraftHeads.DetectSidecar(files);
            if (kind is null)
            {
                Common.Log.Debug($"draft heads: '{repo}' ships none");
                return "";
            }

            var specType = Common.DraftHeads.SpecType(kind.Value);
            Common.Log.Info($"draft heads: '{repo}' ships a {kind.Value} head (spec-type={specType}) — " +
                            "speculative decoding will engage on the model's next load");
            return specType;
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, $"draft-head detection failed for '{modelId}'");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Persists a completed detection and reloads the server's model presets,
    /// so the model's next load drafts with the head. Awaiting the
    /// <paramref name="detection"/> task is safe even if it is still in
    /// flight — the Hub query bounds itself. Fire-and-forget safe.
    /// </summary>
    public static async Task ApplyDetectedAsync(string modelId, Task<string?> detection)
    {
        try
        {
            var specType = await detection;
            if (specType is not null && Apply(modelId, specType))
                await Llama.LlamaManager.Shared.ReloadModelPresetsAsync();
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, $"draft-head apply failed for '{modelId}'");
        }
    }

    /// <summary>
    /// Detects the draft heads of one model and applies the result: the spec
    /// type is recorded in the settings (persisted) and the server's model
    /// presets are reloaded so the model's next load drafts with the head.
    /// Re-detects even when a cached answer exists — a re-download may have
    /// brought new sidecars. Fire-and-forget safe.
    /// </summary>
    public static Task DetectAndApplyAsync(string modelId) =>
        ApplyDetectedAsync(modelId, DetectAsync(modelId));

    /// <summary>
    /// Records the spec type, persisting the settings when it changed.
    /// Returns true when the value is new (the caller reloads the presets).
    /// The settings dictionary is the same reference the server-launch path
    /// renders the INI from, so mutations take the dictionary's lock —
    /// LlamaManager's renderer snapshots under the same lock.
    /// </summary>
    private static bool Apply(string modelId, string specType)
    {
        var dict = Settings.Current.ModelSpeculativeTypes;
        lock (dict)
        {
            if (dict.TryGetValue(modelId, out var existing) && existing == specType)
                return false;
            dict[modelId] = specType;
        }
        Settings.Current.Save();
        return true;
    }

    /// <summary>
    /// Tells the user a loaded model's freshly detected draft head needs a
    /// reload to engage. Toasts are unavailable in some dev registrations —
    /// the log line carries the same information there.
    /// </summary>
    private static void NotifyLoadedMissed(string modelId)
    {
        Common.Log.Info($"draft heads: '{modelId}' is loaded without its draft head — a reload engages it");
        Notifications.Show("Faster drafting available",
            $"'{modelId}' supports speculative decoding — reload it and generation gets up to ~2x faster.",
            new ToastAction("Reload now",
                ("action", "reloadDraftHead"),
                ("id", modelId)));
    }
}
