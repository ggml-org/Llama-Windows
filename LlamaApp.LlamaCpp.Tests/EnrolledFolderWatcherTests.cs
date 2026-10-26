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
    [InlineData("desktop.ini", true)]
    [InlineData("Desktop.INI", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("THUMBS.DB", true)]
    [InlineData(".DS_Store", true)]
    [InlineData("~$word.docx", true)]
    [InlineData("~report.docx", true)]
    [InlineData("model.tmp", true)]
    [InlineData("MODEL.TMP", true)]
    [InlineData("scratch.temp", true)]
    [InlineData("sub/dir/Thumbs.db", true)] // events carry relative paths
    [InlineData("model.gguf", false)]
    [InlineData("model-00001-of-00002.gguf", false)]
    [InlineData("notes.txt", false)]
    [InlineData("desktop.ini.bak", false)]
    [InlineData("mytmp", false)]
    [InlineData("", false)]
    public void IsNoiseName_FiltersOsAndEditorChurn(string name, bool expected)
        => Assert.Equal(expected, EnrolledFolderWatcher.IsNoiseName(name));

    [Fact]
    public void IsNoiseName_RejectsNull()
        => Assert.False(EnrolledFolderWatcher.IsNoiseName(null));

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
    public async Task Any_Name_Change_Fires_One_Callback()
    {
        // Folder move-in/rename fix (issue #135): a directory name event
        // changes what the scan sees even when no .gguf name changed, so EVERY
        // name change (NotifyFilter = FileName | DirectoryName) marks dirty —
        // non-model names included — and the debounce coalesces the burst.
        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        File.WriteAllText(Path.Combine(_root, "notes.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "cover.png"), "2");
        File.Delete(Path.Combine(_root, "notes.txt"));

        Assert.True(await counter.WaitForAsync(1), "name changes should fire a callback");
        Assert.True(await StaysQuietAsync(counter), "the burst must coalesce into ONE callback");
    }

    [Fact]
    public async Task Creating_A_Subdirectory_Fires_A_Callback()
    {
        // Creating a folder inside a watched one can add whole trees of models
        // without a single .gguf name event at the watched level — the scan
        // must refresh just the same.
        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        Directory.CreateDirectory(Path.Combine(_root, "new-folder"));

        Assert.True(await counter.WaitForAsync(1), "creating a subdirectory should fire a callback");
        Assert.True(await StaysQuietAsync(counter), "one directory burst must coalesce into ONE callback");
    }

    [Fact]
    public async Task Moving_A_Folder_In_Fires_A_Callback()
    {
        // A folder of models moved into a watched folder: the scanner sees
        // brand-new models without any .gguf event — the move itself must
        // refresh.
        var staging = Path.Combine(
            Path.GetTempPath(), "llama-watch-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "moved-model.gguf"), "1");
        try
        {
            var counter = new Counter();
            using var watcher = new EnrolledFolderWatcher(
                [_root], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

            Directory.Move(staging, Path.Combine(_root, "moved-in"));

            Assert.True(await counter.WaitForAsync(1), "moving a folder in should fire a callback");
            Assert.True(await StaysQuietAsync(counter), "the move must coalesce into ONE callback");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best-effort */ }
        }
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

    [Fact]
    public void A_File_Where_A_Folder_Should_Be_Is_Reported_Not_Watched()
    {
        // A renamed/replaced path can land on a file — same expected state,
        // same treatment (and no exception escaping the constructor).
        var file = Path.Combine(_root, "just-a-file");
        File.WriteAllText(file, "1");

        var counter = new Counter();
        using var watcher = new EnrolledFolderWatcher(
            [file], counter.Fire, quiet: TimeSpan.FromMilliseconds(250), maxLatency: TimeSpan.FromSeconds(3));

        Assert.Empty(watcher.WatchedFolders);
        Assert.Contains(file, watcher.UnhealthyFolders);
    }
}
