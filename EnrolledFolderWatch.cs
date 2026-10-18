using System.Threading;

namespace LlamaApp;

/// <summary>
/// Keeps the enrolled model folders live: watches them for <c>.gguf</c>
/// changes (a model added/deleted/renamed in Explorer) and refreshes the
/// server's view — re-rendering the <c>--models-preset</c> and asking the
/// router to rescan — so the /models poller reconciles the list without a
/// restart.
///
/// <para>Two entry points, one refresh path: <see cref="StartWatching"/> wires
/// the event-driven watcher (burst-debounced), and
/// <see cref="RefreshOnOpen"/> is the flyout-open fallback for folders whose
/// watcher went quietly broken (SMB shares without change notifications are
/// the common case) — rate-limited so focus flaps can't spam reloads. Folder
/// list changes re-point the watcher via <see cref="SyncFolders"/>.</para>
///
/// <para>Mirrors <see cref="DraftHeadDetection"/>'s static-watcher shape: the
/// poller and UI stay untouched, everything funnels through
/// <c>ReloadModelPresetsAsync</c>.</para>
/// </summary>
internal static class EnrolledFolderWatch
{
    /// <summary>Fallback refresh cooldown — the flyout can open/close rapidly.</summary>
    private const long RefreshCooldownMs = 30_000;

    private static Llama.EnrolledFolderWatcher? _watcher;
    private static long _lastRefreshMs = long.MinValue;

    /// <summary>
    /// Starts watching the enrolled folders. Safe to call once per app
    /// session; later calls are no-ops.
    /// </summary>
    public static void StartWatching()
    {
        if (_watcher is not null) return;
        _watcher = new Llama.EnrolledFolderWatcher(
            Settings.Current.ModelFolders,
            OnFoldersChangedAsync);
        Common.Log.Info($"model folder watch started ({_watcher.WatchedFolders.Count} folder(s))");
    }

    /// <summary>
    /// Re-points the watcher after the enrolled-folder list changed (the
    /// Settings commit). Retries folders that failed earlier.
    /// </summary>
    public static void SyncFolders()
    {
        _watcher?.UpdateFolders(Settings.Current.ModelFolders);
    }

    /// <summary>
    /// Flyout-open fallback: refresh at most once per
    /// <see cref="RefreshCooldownMs"/>. Covers watchers that died quietly and
    /// any change the watcher class filtered out.
    /// </summary>
    public static void RefreshOnOpen()
    {
        if (_watcher is null) return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRefreshMs);
        if (now - last < RefreshCooldownMs) return;
        if (Interlocked.CompareExchange(ref _lastRefreshMs, now, last) != last) return;

        _ = ReloadAsync("flyout open");
    }

    private static Task OnFoldersChangedAsync() => ReloadAsync("folder change");

    private static async Task ReloadAsync(string reason)
    {
        try
        {
            Common.Log.Debug($"enrolled-folder refresh ({reason})");
            await Llama.LlamaManager.Shared.ReloadModelPresetsAsync();
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, "enrolled-folder refresh failed");
        }
    }
}
