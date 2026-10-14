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
/// <para><b>When it runs:</b> once per model id at download start (the entry
/// is ready before the first load), plus a startup backfill for models
/// already in the HF cache from before the feature existed. Empty-string
/// entries are a negative cache ("checked, none shipped") so the Hub is not
/// re-queried every session; an explicit download re-checks. Sidecars beside
/// imported local models need no help: the server's <c>--models-dir</c> scan
/// wires them itself (<c>LLAMA_ARG_SPEC_DRAFT_MODEL</c> in
/// <c>preset.cpp:load_from_models_dir</c>).</para>
///
/// <para>All failures are best-effort and logged: detection must never block
/// or fault a download, a load, or startup.</para>
/// </summary>
internal static class DraftHeadDetection
{
    /// <summary>
    /// The repo part of a server model id (see
    /// <see cref="Common.DraftHeads.RepoFromModelId"/>).
    /// </summary>
    internal static string RepoFromModelId(string modelId) => Common.DraftHeads.RepoFromModelId(modelId);

    /// <summary>
    /// Queries the repo's file listing and classifies its draft heads.
    /// Returns the spec-type token (<c>draft-dflash</c>, …), or the empty
    /// string when the repo ships none (the negative cache), or
    /// <c>null</c> when the listing failed (nothing is recorded — a later
    /// session retries). Does NOT record anything — callers combine it with
    /// <see cref="ApplyDetectedAsync"/> so a detection that races a failed or
    /// canceled download is never persisted (an INI entry for a model that
    /// isn't in the HF cache would list a ghost model the router can't load).
    /// </summary>
    public static async Task<string?> DetectAsync(string modelId)
    {
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
    /// One-shot per session: detects draft heads for models already known to
    /// the server (the HF cache) that were never checked — backfills installs
    /// from before the feature existed. Only ids with an owner/repo shape are
    /// considered; a checked id (any entry, including the empty negative
    /// cache) is skipped. A single preset reload at the end if anything
    /// changed.
    /// </summary>
    public static async Task BackfillAsync(CancellationToken cancel = default)
    {
        try
        {
            var mgr = Llama.LlamaManager.Shared;

            // The server may still be installing/starting when the app
            // launches; wait (bounded) for a models snapshot to exist.
            for (var i = 0; i < 60; i++)
            {
                if (mgr.ServerStatus == Llama.LlamaManager.ServerState.Running &&
                    mgr.LastModelsSnapshot.Count > 0)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            }
            if (mgr.ServerStatus != Llama.LlamaManager.ServerState.Running)
                return;

            var candidates = mgr.LastModelsSnapshot
                .Select(m => m.Id)
                .Where(id => id.Contains('/'))
                .Where(id => !Settings.Current.ModelSpeculativeTypes.ContainsKey(id))
                .Distinct()
                .ToList();
            if (candidates.Count == 0)
                return;

            Common.Log.Debug($"draft-head backfill: checking {candidates.Count} cached model(s)");
            var changed = false;
            foreach (var id in candidates)
            {
                var specType = await DetectAsync(id);
                if (specType is not null && Apply(id, specType))
                    changed = true;
            }
            if (changed)
                await mgr.ReloadModelPresetsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, "draft-head backfill failed");
        }
    }

    /// <summary>
    /// Records the spec type, persisting the settings when it changed.
    /// Returns true when the value is new (the caller reloads the presets).
    /// </summary>
    private static bool Apply(string modelId, string specType)
    {
        if (Settings.Current.ModelSpeculativeTypes.TryGetValue(modelId, out var existing) &&
            existing == specType)
            return false;
        Settings.Current.ModelSpeculativeTypes[modelId] = specType;
        Settings.Current.Save();
        return true;
    }
}
