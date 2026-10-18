using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for <see cref="EnrolledFolderWatcher"/>: the .gguf filter, the
/// burst debounce (one refresh per bulk change, not one per event), and
/// folder re-pointing. Drives real filesystem events in a temp directory —
/// generous timeouts, the assertions poll.
/// </summary>
public sealed class EnrolledFolderWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "llama-watch-" + Guid.NewGuid().ToString("N"));

    public EnrolledFolderWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Counts callbacks and lets tests await the n-th.</summary>
    private sealed class Counter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task Fire() { Interlocked.Increment(ref _count); return Task.CompletedTask; }

        /// <summary>Waits up to <paramref name="timeoutMs"/> for the counter to
        /// reach <paramref name="atLeast"/>; returns whether it did.</summary>
        public async Task<bool> WaitForAsync(int atLeast, int timeoutMs = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (Count >= atLeast) return true;
                await Task.Delay(50);
            }
            return Count >= atLeast;
        }
    }

    private static async Task<bool> StaysQuietAsync(Counter counter, int forMs = 1_000)
    {
        var before = counter.Count;
        await Task.Delay(forMs);
        return counter.Count == before;
    }

    [Theory]
    [InlineData("model.gguf", true)]
    [InlineData("MODEL.GGUF", true)]
    [InlineData("sub/dir/model-00001-of-00002.gguf", true)]
    [InlineData("vision-mmproj.gguf", true)]
    [InlineData("mtp-draft.gguf", true)]
    [InlineData("model.gguf.part", false)]
    [InlineData("notes.txt", false)]
    [InlineData("model.bin", false)]
    [InlineData("README.md", false)]
    public void IsModelFile_MatchesOnlyWhatTheScannerSees(string path, bool expected)
        => Assert.Equal(expected, EnrolledFolderWatcher.IsModelFile(path));

    [Fact]
    public void IsModelFile_RejectsNull()
        => Assert.False(EnrolledFolderWatcher.IsModelFile(null));

    [Fact]
    public async Task A_Burst_Of_Model_Changes_Fires_One_Callback()
    {
        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        File.WriteAllText(Path.Combine(_root, "a.gguf"), "1");
        File.WriteAllText(Path.Combine(_root, "b.gguf"), "2");
        File.WriteAllText(Path.Combine(_root, "c.gguf"), "3");
        File.Delete(Path.Combine(_root, "a.gguf"));

        Assert.True(await counter.WaitForAsync(1), "the burst should have fired a callback");
        Assert.True(await StaysQuietAsync(counter), "a burst must coalesce into ONE callback");
    }

    [Fact]
    public async Task Deleting_A_Model_File_Fires_A_Callback()
    {
        var file = Path.Combine(_root, "gone.gguf");
        File.WriteAllText(file, "1");

        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        File.Delete(file);

        Assert.True(await counter.WaitForAsync(1), "deleting a .gguf should fire a callback");
    }

    [Fact]
    public async Task Renaming_A_Model_File_Fires_A_Callback()
    {
        var file = Path.Combine(_root, "old.gguf");
        File.WriteAllText(file, "1");

        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        File.Move(file, Path.Combine(_root, "new.gguf"));

        Assert.True(await counter.WaitForAsync(1), "renaming a .gguf should fire a callback");
    }

    [Fact]
    public async Task Changes_Outside_Model_Files_Are_Ignored()
    {
        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        File.WriteAllText(Path.Combine(_root, "notes.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "cover.png"), "2");
        File.Delete(Path.Combine(_root, "notes.txt"));

        Assert.True(await StaysQuietAsync(counter, forMs: 1_500), "non-model files must not trigger refreshes");
    }

    [Fact]
    public async Task Subdirectory_Changes_Are_Watched()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        File.WriteAllText(Path.Combine(sub, "deep.gguf"), "1");

        Assert.True(await counter.WaitForAsync(1), "nested .gguf changes should fire a callback");
    }

    [Fact]
    public async Task UpdateFolders_Picks_Up_New_And_Removed_Folders()
    {
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(second);
        var third = Path.Combine(_root, "third");
        Directory.CreateDirectory(third);

        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [second], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        // Re-point: third joins, second leaves.
        watcher.UpdateFolders([third]);
        Assert.Contains(third, watcher.WatchedFolders);
        Assert.DoesNotContain(second, watcher.WatchedFolders);

        File.WriteAllText(Path.Combine(third, "new.gguf"), "1");
        Assert.True(await counter.WaitForAsync(1), "the newly watched folder should fire");

        Assert.True(await StaysQuietAsync(counter), "no stray callbacks from the re-point");
    }

    [Fact]
    public void Missing_Folders_Are_Reported_Not_Watched()
    {
        var missing = Path.Combine(_root, "not-there");
        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [missing], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        Assert.Empty(watcher.WatchedFolders);
        Assert.Contains(missing, watcher.UnhealthyFolders);
    }
}
