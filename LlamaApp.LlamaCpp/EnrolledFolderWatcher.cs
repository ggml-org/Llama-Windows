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

    /// <summary>
    /// Whether an event name is pure OS/app churn — Explorer's
    /// <c>desktop.ini</c>/<c>Thumbs.db</c>, macOS's <c>.DS_Store</c>, Office
    /// lock files (<c>~$doc.docx</c>, any <c>~</c>-prefixed name) and editor
    /// scratch files (<c>*.tmp</c>/<c>*.temp</c>). A churn folder (Downloads)
    /// fires these constantly; none of them can be a model, so reacting to
    /// them would sustain a full recursive rescan + INI write + server
    /// reload on every quiet period for nothing.
    /// </summary>
    internal static bool IsNoiseName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Events under IncludeSubdirectories carry a relative path — the
        // rules apply to the final component.
        name = Path.GetFileName(name);
        // A .gguf name can ALWAYS change the scan result (the scanner
        // enumerates *.gguf) — never classify one as noise, whatever
        // prefix/suffix it carries (~model.gguf is a legitimate model).
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith('~') // includes the Office ~$ lock prefix
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // ANY name change marks dirty: NotifyFilter is FileName|DirectoryName,
        // so a directory event (a folder of models created, moved in or
        // renamed) changes what the scan sees even when no .gguf name did.
        // The debounce coalesces the burst into one refresh. Noise names are
        // the one exception — they can never change the scan result.
        if (!IsNoiseName(e.Name)) MarkDirty();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename out of a noise name (or into one) can still matter — the
        // non-noise side is what the scan may see.
        if (!IsNoiseName(e.Name) || !IsNoiseName(e.OldName)) MarkDirty();
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
