using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the managed-models-dir migration (issue #107's sibling): the
/// pre-fix dir lived under the MSIX-virtualized %LOCALAPPDATA%\Llama, where
/// the out-of-package llama server could never see imported models. On first
/// use the content moves to the never-virtualized user-profile location. The
/// migration must be idempotent and retry-safe (a resumed run completes only
/// what is left), must prefer existing target entries over legacy ones, and
/// must never fault the launch for a file it cannot move.
/// </summary>
public sealed class ManagedModelsDirMigrationTests : IDisposable
{
    private readonly string _root;

    public ManagedModelsDirMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-modelfolders-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Legacy => Path.Combine(_root, "legacy");
    private string Target => Path.Combine(_root, "target");

    private string WriteLegacy(string name, byte[]? content = null)
    {
        var path = Path.Combine(Legacy, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? new byte[64]);
        return path;
    }

    [Fact]
    public void MovesFilesAndSubdirectories_IntoTheTarget()
    {
        WriteLegacy("imported-model-Q4_0.gguf");
        WriteLegacy(Path.Combine("imported-dir", "model.gguf"));
        WriteLegacy(Path.Combine("imported-dir", "mmproj-model.gguf"));

        var moved = LlamaManager.MigrateManagedModelsDir(Legacy, Target);

        Assert.Equal(2, moved);
        Assert.True(File.Exists(Path.Combine(Target, "imported-model-Q4_0.gguf")));
        Assert.True(File.Exists(Path.Combine(Target, "imported-dir", "model.gguf")));
        Assert.True(File.Exists(Path.Combine(Target, "imported-dir", "mmproj-model.gguf")));
        Assert.False(Directory.Exists(Legacy)); // emptied → removed
    }

    [Fact]
    public void KeepsExistingTargetEntries_AndResumesOnlyTheRemainder()
    {
        // A partially migrated dir: the target already has one of the two
        // files (identical name) — that one must not be clobbered, the other
        // must move, and a second call must be a no-op.
        WriteLegacy("already-there.gguf", new byte[] { 1 });
        WriteLegacy("still-in-legacy.gguf", new byte[] { 2 });
        Directory.CreateDirectory(Target);
        File.WriteAllBytes(Path.Combine(Target, "already-there.gguf"), new byte[] { 9 });

        var first = LlamaManager.MigrateManagedModelsDir(Legacy, Target);
        var second = LlamaManager.MigrateManagedModelsDir(Legacy, Target);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(Target, "already-there.gguf")));
        Assert.True(File.Exists(Path.Combine(Target, "still-in-legacy.gguf")));
    }

    [Fact]
    public void MissingLegacyDir_IsANoOp()
    {
        Assert.Equal(0, LlamaManager.MigrateManagedModelsDir(Legacy, Target));
        Assert.False(Directory.Exists(Target)); // nothing was even created
    }

    [Fact]
    public void AnUnmovableEntry_IsLeftBehind_NeverFaults()
    {
        var locked = WriteLegacy("locked.gguf");
        WriteLegacy("free.gguf");

        // Hold the file open without write share — Move fails with an
        // IOException; the migration must swallow it and continue.
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var moved = LlamaManager.MigrateManagedModelsDir(Legacy, Target);

            Assert.Equal(1, moved);
            Assert.True(File.Exists(Path.Combine(Target, "free.gguf")));
            Assert.True(File.Exists(locked)); // left behind
            Assert.True(Directory.Exists(Legacy)); // not removed — still has an entry
        }

        // With the lock released the migration completes.
        Assert.Equal(1, LlamaManager.MigrateManagedModelsDir(Legacy, Target));
        Assert.False(Directory.Exists(Legacy));
    }
}
