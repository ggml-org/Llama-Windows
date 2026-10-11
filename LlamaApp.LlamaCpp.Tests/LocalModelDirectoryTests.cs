using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

public sealed class LocalModelDirectoryTests
{
    [Fact]
    public async Task DeletingSelectedModelLeavesOtherFilesAndRefreshesTheSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-local-delete-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var selected = Path.Combine(root, "Model-Q4_K_M.gguf");
            var other = Path.Combine(root, "Model-Q8_0.gguf");
            var projector = Path.Combine(root, "mmproj-Model.gguf");
            File.WriteAllText(selected, "GGUF");
            File.WriteAllText(other, "GGUF");
            File.WriteAllText(projector, "GGUF");
            var cache = new LocalModelDirectoryCache();
            await cache.RefreshAsync([root]);
            var id = cache.Snapshot.Single(m => m.Value == selected).Key;

            LocalModelDirectory.Delete(id, [root]);
            await cache.RefreshAsync([root], force: true);

            Assert.False(File.Exists(selected));
            Assert.True(File.Exists(other));
            Assert.True(File.Exists(projector));
            Assert.True(Directory.Exists(root));
            Assert.False(cache.Snapshot.ContainsKey(id));
            Assert.Single(cache.Snapshot);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DeletionRejectsAnIdFromARemovedFolderOrAnUntrustedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-local-delete-boundary-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Model.gguf");
            File.WriteAllText(file, "GGUF");
            var id = Assert.Single(LocalModelDirectory.Scan([root])).Key;

            Assert.Throws<IOException>(() => LocalModelDirectory.Delete(id, []));
            Assert.Throws<IOException>(() => LocalModelDirectory.Delete(file, [root]));
            Assert.Throws<IOException>(() => LocalModelDirectory.Delete("local/../../Model", [root]));
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LockedModelDeletionFailsWithoutRemovingTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-local-delete-locked-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Model.gguf");
            File.WriteAllText(file, "GGUF");
            var id = Assert.Single(LocalModelDirectory.Scan([root])).Key;
            using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<IOException>(() => LocalModelDirectory.Delete(id, [root]));
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BackgroundScanKeepsPreviousSnapshotReadableUntilComplete()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        SynchronizationContext? scanContext = null;
        var scans = 0;
        var cache = new LocalModelDirectoryCache(roots =>
        {
            scans++;
            if (scans == 2)
            {
                scanContext = SynchronizationContext.Current;
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test did not release the background scan.");
            }
            return new Dictionary<string, string> { [roots[0]] = roots[0] + ".gguf" };
        });
        await cache.RefreshAsync(["first"]);
        var previous = cache.Snapshot;
        Task refresh;
        var context = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            refresh = cache.RefreshAsync(["second"]);
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(refresh.IsCompleted);
            Assert.Null(scanContext);
            Assert.Same(previous, cache.Snapshot);
            Assert.Equal("first.gguf", cache.Snapshot["first"]);
        }
        finally { release.Set(); }
        await refresh;
        Assert.Equal("second.gguf", cache.Snapshot["second"]);
        Assert.False(cache.Snapshot.ContainsKey("first"));
        Assert.True(previous.ContainsKey("first"));
    }

    [Fact]
    public async Task UnchangedFoldersReuseSnapshotAndExplicitRefreshFindsNewFiles()
    {
        var scans = 0;
        var cache = new LocalModelDirectoryCache(_ =>
            new Dictionary<string, string> { ["model"] = $"{Interlocked.Increment(ref scans)}.gguf" });
        await Task.WhenAll(cache.RefreshAsync([@"C:\Models"]), cache.RefreshAsync([@"c:\models"]));
        Assert.Equal(1, scans);
        var snapshot = cache.Snapshot;
        await cache.RefreshAsync([@"C:\Models"]);
        Assert.Same(snapshot, cache.Snapshot);
        await cache.RefreshAsync([@"C:\Models"], force: true);
        Assert.Equal(2, scans);
        Assert.Equal("2.gguf", cache.Snapshot["model"]);
    }

    [Fact]
    public async Task RemovingFoldersClearsSnapshot()
    {
        var cache = new LocalModelDirectoryCache(roots => roots.ToDictionary(root => root, root => root));
        await cache.RefreshAsync(["folder"]);
        Assert.Single(cache.Snapshot);
        await cache.RefreshAsync([]);
        Assert.Empty(cache.Snapshot);
    }

    [Fact]
    public async Task CancelledRefreshDoesNotReplaceSnapshot()
    {
        using var cancel = new CancellationTokenSource();
        var cache = new LocalModelDirectoryCache(roots =>
        {
            if (roots[0] == "cancelled") cancel.Cancel();
            return new Dictionary<string, string> { [roots[0]] = roots[0] };
        });
        await cache.RefreshAsync(["first"]);
        var previous = cache.Snapshot;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.RefreshAsync(["cancelled"], cancel.Token));
        Assert.Same(previous, cache.Snapshot);
        await cache.RefreshAsync(["cancelled"]);
        Assert.True(cache.Snapshot.ContainsKey("cancelled"));
    }

    [Fact]
    public void CombinesFoldersAndDeduplicatesOverlappingRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-multi-folder-test-" + Guid.NewGuid());
        try
        {
            var first = Path.Combine(root, "A", "First.gguf");
            var second = Path.Combine(root, "B", "Second.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "GGUF");
            File.WriteAllText(second, "GGUF");

            var models = LocalModelDirectory.Scan([Path.Combine(root, "missing"),
                Path.Combine(root, "A"), root, Path.Combine(root, "B")]);
            Assert.Equal(2, models.Count);
            Assert.Contains(first, models.Values);
            Assert.Contains(second, models.Values);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindsNestedWeightsWithStableDistinctIdsAndSkipsProjectors()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-local-model-test-" + Guid.NewGuid());
        try
        {
            var first = Path.Combine(root, "Publisher A", "Model.gguf");
            var second = Path.Combine(root, "Publisher B", "Model.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "GGUF");
            File.WriteAllText(second, "GGUF");
            File.WriteAllText(Path.Combine(root, "Publisher B", "mmproj-Model.gguf"), "GGUF");

            var models = LocalModelDirectory.Scan([root]);
            Assert.Equal(2, models.Count);
            Assert.Contains(first, models.Values);
            Assert.Contains(second, models.Values);
            Assert.Equal(models.Keys.Order(), LocalModelDirectory.Scan([root]).Keys.Order());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
