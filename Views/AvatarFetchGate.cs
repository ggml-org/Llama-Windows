namespace LlamaApp.Views;

/// <summary>
/// Coalesces concurrent avatar fetches per author and bounds the total number
/// of in-flight fetches. Installed-model lists can populate dozens of rows at
/// once from a handful of orgs, so a naive per-row fetch would open a request
/// storm against the Hub. This gate keeps at most one underlying fetch in
/// flight per author (case-insensitive) and caps global concurrency, while
/// remaining fail-soft: a failed fetch leaves no residue, so the next
/// population attempt may retry.
///
/// Pure managed and storage-free — production supplies a Hub fetch delegate
/// (<see cref="AvatarCache"/>); unit tests inject a counting/gating stub.
/// </summary>
internal sealed class AvatarFetchGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Task<byte[]?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _semaphore;
    private readonly Func<string, Task<byte[]?>> _fetch;

    internal AvatarFetchGate(int maxConcurrency, Func<string, Task<byte[]?>> fetch)
    {
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _fetch = fetch;
    }

    /// <summary>
    /// Runs <paramref name="author"/>'s fetch, sharing one underlying call with
    /// every concurrent caller for the same author. Returns null for a blank
    /// author without invoking the delegate; a faulting fetch propagates to
    /// every caller, which is expected to fail soft.
    /// </summary>
    internal async Task<byte[]?> FetchAsync(string author)
    {
        if (string.IsNullOrWhiteSpace(author)) return null;

        Task<byte[]?> shared;
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(author, out var existing))
            {
                existing = FetchCoreAsync(author);
                _inFlight[author] = existing;
            }
            shared = existing;
        }

        return await shared.ConfigureAwait(false);
    }

    private async Task<byte[]?> FetchCoreAsync(string author)
    {
        // This method is invoked while _sync is held, and an async method runs
        // its synchronous prefix inline — yield first so the semaphore wait and
        // the fetch delegate only ever run after the lock is released.
        await Task.Yield();

        try
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _fetch(author).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        finally
        {
            // Drop the in-flight entry on success AND failure so a completed or
            // failed author is never negative-cached here — the resolved-image
            // cache lives in AvatarCache.Images.
            lock (_sync) _inFlight.Remove(author);
        }
    }
}
