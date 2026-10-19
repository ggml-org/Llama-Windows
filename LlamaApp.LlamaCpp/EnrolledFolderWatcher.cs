using System.IO;

namespace LlamaApp.Llama;

/// <summary>
/// Watches enrolled model folders for model-file changes — a <c>.gguf</c>
/// added, deleted or renamed in Explorer is exactly what the on-disk scan
/// would pick up or drop — and invokes one callback after a quiet period, so
/// a bulk copy/delete produces one refresh instead of an event storm.
///
/// <para>A watcher that goes quiet-but-broken (SMB shares without change
/// notifications, internal buffer overflow) is folded into
/// <see cref="UnhealthyFolders"/> and stops producing callbacks; the caller's
/// refresh-on-open fallback covers those folders instead of pretending the
/// events still flow. Internal for tests.</para>
/// </summary>
public sealed class EnrolledFolderWatcher : IDisposable
{
    private readonly Func<Task> _onChanged;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxLatency;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _unhealthy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounce;
    private DateTime _firstDirtyUtc;
    private bool _dirty;
    private bool _disposed;

    /// <param name="folders">Folders to watch (recursively).</param>
    /// <param name="onChanged">Invoked once per burst of changes.</param>
    /// <param name="quiet">Quiet period before firing — the debounce window.</param>
    /// <param name="maxLatency">Upper bound on how long a sustained storm of
    /// events may postpone the callback.</param>
    public EnrolledFolderWatcher(
        IEnumerable<string> folders,
        Func<Task> onChanged,
        TimeSpan? quiet = null,
        TimeSpan? maxLatency = null)
    {
        _onChanged = onChanged;
        _quiet = quiet ?? TimeSpan.FromSeconds(2);
        _maxLatency = maxLatency ?? TimeSpan.FromSeconds(10);
        Rebuild(folders);
    }

    /// <summary>Folders whose watcher failed and is not being watched.</summary>
    public IReadOnlyCollection<string> UnhealthyFolders
    {
        get { lock (_gate) return _unhealthy.ToArray(); }
    }

    /// <summary>Folders currently watched.</summary>
    public IReadOnlyCollection<string> WatchedFolders
    {
        get { lock (_gate) return _watched.ToArray(); }
    }

    /// <summary>
    /// Re-points the watchers after the enrolled-folder list changed. Folders
    /// that failed before are retried (a NAS that was offline comes back).
    /// </summary>
    public void UpdateFolders(IEnumerable<string> folders) => Rebuild(folders);

    /// <summary>
    /// Whether a path is a file the folder scan cares about: the scanner
    /// enumerates <c>*.gguf</c> and treats mmproj/draft/shard special names
    /// within that set — so <c>.gguf</c> is the whole universe.
    /// </summary>
    public static bool IsModelFile(string? path)
        => path is not null && path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    private void Rebuild(IEnumerable<string> folders)
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileEvent;
                watcher.Deleted -= OnFileEvent;
                watcher.Renamed -= OnRenamed;
                watcher.Dispose();
            }
            _watchers.Clear();
            _watched.Clear();
            _unhealthy.Clear();

            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (!Directory.Exists(folder))
                {
                    // An enrolled path that isn't (or is no longer) a
                    // directory — removed or renamed by the user: expected,
                    // not a watcher fault. Reported and skipped; the
                    // refresh-on-open fallback covers its return.
                    MarkUnhealthy(folder, null, missing: true);
                    continue;
                }
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    };
                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, args) => MarkUnhealthy(folder, args.GetException());
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    _watched.Add(folder);
                }
                catch (Exception ex)
                {
                    MarkUnhealthy(folder, ex);
                }
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsModelFile(e.Name)) MarkDirty();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsModelFile(e.Name) || IsModelFile(e.OldName)) MarkDirty();
    }

    private void MarkUnhealthy(string folder, Exception? ex, bool missing = false)
    {
        lock (_gate)
        {
            _watched.Remove(folder);
            _unhealthy.Add(folder);
        }
        // A path that is not a directory (never existed, removed, renamed) is
        // an expected state, not a watcher crash — message only. Anything
        // else is a real watcher failure and keeps its stack.
        if (missing || ex is ArgumentException)
            Common.Log.Warn($"model folder not present; not watching it: {folder}");
        else
            Common.Log.Warn(ex, $"model folder watcher failed; falling back to refresh-on-open: {folder}");
    }

    /// <summary>
    /// Debounce core: coalesce events into one callback after a quiet period,
    /// never postponing past the max-latency deadline from the first event of
    /// a sustained storm.
    /// </summary>
    private void MarkDirty()
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_dirty)
            {
                _dirty = true;
                _firstDirtyUtc = DateTime.UtcNow;
            }
            var untilDeadline = _maxLatency - (DateTime.UtcNow - _firstDirtyUtc);
            wait = untilDeadline < _quiet ? (untilDeadline < TimeSpan.Zero ? TimeSpan.Zero : untilDeadline) : _quiet;

            _debounce ??= new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounce.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_disposed || !_dirty) return;
            _dirty = false;
        }
        _ = FireAsync();
    }

    private async Task FireAsync()
    {
        try
        {
            await _onChanged();
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, "model folder change handler failed");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
        }
    }
}
