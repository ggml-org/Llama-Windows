using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the "which"-style binary resolution
/// (<see cref="LlamaManager.FindOnPath(string, string?, string?, string?)"/>)
/// and the managed-vs-external classification (<see cref="LlamaManager.IsManagedPath"/>).
/// The case that matters operationally: right after <c>install.ps1</c> runs,
/// the binary is only visible on the <em>user</em> PATH (registry) — the
/// installer is a child process and cannot update our own environment block —
/// so the lookup must search user/machine PATH beyond the process PATH.
/// </summary>
public sealed class LlamaManagerPathResolutionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llama-path-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Creates a temp dir containing a fake <c>llama.exe</c> and returns the dir.</summary>
    private string MakeInstallDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "llama.exe"), "fake");
        return dir;
    }

    [Fact]
    public void FindOnPath_FindsExeOnProcessPath()
    {
        var dir = MakeInstallDir("a");
        Assert.Equal(
            Path.Combine(dir, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", dir, null, null));
    }

    [Fact]
    public void FindOnPath_FindsExeOnUserPath_WhenNotOnProcessPath()
    {
        // The post-install case: install.ps1 put its dir on the user PATH, but
        // our process environment block doesn't have it.
        var dir = MakeInstallDir("a");
        Assert.Equal(
            Path.Combine(dir, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", @"C:\Windows", dir, null));
    }

    [Fact]
    public void FindOnPath_FindsExeOnMachinePath()
    {
        var dir = MakeInstallDir("a");
        Assert.Equal(
            Path.Combine(dir, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", null, null, dir));
    }

    [Fact]
    public void FindOnPath_ProcessPathWinsOverUserPath()
    {
        var processDir = MakeInstallDir("process");
        var userDir = MakeInstallDir("user");
        Assert.Equal(
            Path.Combine(processDir, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", processDir, userDir, null));
    }

    [Fact]
    public void FindOnPath_FirstDirInListWins()
    {
        var first = MakeInstallDir("first");
        var second = MakeInstallDir("second");
        Assert.Equal(
            Path.Combine(first, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", first + Path.PathSeparator + second, null, null));
    }

    [Fact]
    public void FindOnPath_ReturnsNull_WhenNotFoundAnywhere()
    {
        Assert.Null(LlamaManager.FindOnPath("llama.exe", _root, _root, _root));
    }

    [Fact]
    public void FindOnPath_ReturnsNull_WhenAllPathsAreNullOrEmpty()
    {
        Assert.Null(LlamaManager.FindOnPath("llama.exe", null, "", "  "));
    }

    [Fact]
    public void FindOnPath_ToleratesQuotedAndPaddedEntries()
    {
        var dir = MakeInstallDir("a");
        var pathEnv = $"  \"{dir}\"  ";
        Assert.Equal(
            Path.Combine(dir, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", pathEnv, null, null));
    }

    [Fact]
    public void FindOnPath_SkipsMalformedEntries()
    {
        var dir = MakeInstallDir("a");
        // A NUL-containing entry is invalid; it must be skipped, not throw.
        var pathEnv = "bad\0entry" + Path.PathSeparator + dir;
        Assert.Equal(
            Path.Combine(dir, "llama.exe"),
            LlamaManager.FindOnPath("llama.exe", pathEnv, null, null));
    }

    [Fact]
    public void IsManagedPath_TrueForBinaryInInstallDir()
    {
        var binary = Path.Combine(LlamaManager.ManagedInstallDir, "llama.exe");
        Assert.True(LlamaManager.IsManagedPath(binary));
    }

    [Fact]
    public void IsManagedPath_IsCaseInsensitive()
    {
        var binary = Path.Combine(LlamaManager.ManagedInstallDir.ToUpperInvariant(), "LLAMA.EXE");
        Assert.True(LlamaManager.IsManagedPath(binary));
    }

    [Fact]
    public void IsManagedPath_FalseForBinaryElsewhere()
    {
        Assert.False(LlamaManager.IsManagedPath(Path.Combine(_root, "llama.exe")));
    }
}
