namespace LlamaApp.Views;

/// <summary>
/// Tracks how many consecutive authoritative model snapshots have *not* listed
/// a row's model — the hysteresis behind removing rows whose files were
/// deleted outside the app (Explorer, a cleaner tool). A model must vanish
/// from N consecutive snapshots before its row goes: one missed listing (a
/// rescan in flight, a briefly unreachable network folder) must never flicker
/// rows away. Pure so it stays unit-testable.
/// </summary>
public sealed class MissingSnapshotTracker
{
    private readonly int _threshold;
    private readonly Dictionary<string, int> _misses = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="threshold">Consecutive misses before a row expires. The
    /// poller's snapshot cadence is 500 ms–2 s, so 3 misses means "gone for
    /// seconds", not "gone for a tick".</param>
    public MissingSnapshotTracker(int threshold = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        _threshold = threshold;
    }

    /// <summary>
    /// Records one authoritative snapshot: ids present in
    /// <paramref name="seenIds"/> have their miss count cleared; ids in
    /// <paramref name="rowIds"/> that were missed again accrue one; ids that
    /// reach the threshold are returned (and forgotten) so the caller can drop
    /// their rows.
    /// </summary>
    public IReadOnlyList<string> Observe(
        IReadOnlyCollection<string> rowIds, IReadOnlyCollection<string> seenIds)
    {
        var seen = new HashSet<string>(seenIds, StringComparer.OrdinalIgnoreCase);
        var expired = new List<string>();

        foreach (var id in rowIds)
        {
            if (seen.Contains(id))
            {
                _misses.Remove(id);
                continue;
            }

            var misses = _misses.TryGetValue(id, out var count) ? count + 1 : 1;
            if (misses >= _threshold)
            {
                _misses.Remove(id);
                expired.Add(id);
            }
            else
            {
                _misses[id] = misses;
            }
        }

        // Rows that no longer exist must not keep accruing.
        foreach (var id in _misses.Keys.Where(id => !rowIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList())
            _misses.Remove(id);

        return expired;
    }
}
