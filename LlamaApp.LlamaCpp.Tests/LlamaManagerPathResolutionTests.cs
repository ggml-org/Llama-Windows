using System.Diagnostics;
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
    public void EffectivePath_NormalizesAndDeduplicatesInDiscoveryOrder()
    {
        var process = MakeInstallDir("process");
        var user = MakeInstallDir("user");
        var machine = MakeInstallDir("machine");
        var actual = LlamaManager.EffectivePath(
            $"  \"{process}\"  ;{process.ToUpperInvariant()}",
            $";{process};{user};bad\0entry", $"{user};{machine}");
        Assert.Equal(string.Join(Path.PathSeparator, process, user, machine), actual);
        Assert.Equal("", LlamaManager.EffectivePath(null, "", "  "));
    }

    [Theory]
    [InlineData("ffmpeg.exe")]
    [InlineData("ffprobe.exe")]
    public async Task EffectivePath_MakesNewDecodersVisibleToChildProcesses(string executable)
    {
        var oldPath = MakeInstallDir("old-path");
        var installed = MakeInstallDir("new-decoder");
        var interpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // A real executable with deterministic output, isolated from installed decoders.
        File.Copy(interpreter, Path.Combine(installed, executable));
        Assert.Null(LlamaManager.FindOnPath(executable, oldPath, null, null));
        Assert.Equal(Path.Combine(installed, executable),
            LlamaManager.FindOnPath(executable, oldPath, installed, null));
        var start = new ProcessStartInfo(interpreter)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = LlamaManager.EffectivePath(oldPath, installed, null);
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add($"{executable} /d /c echo decoder-found");
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.ExitCode == 0, await error);
            Assert.Equal("decoder-found", (await output).Trim());
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
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

    // ---- Interpreter path (install script) ----

    [Fact]
    public void PowerShellExePath_IsAbsoluteAndUnderSystemDirectory()
    {
        var path = LlamaManager.PowerShellExePath;

        // A bare "powershell.exe" would resolve through the CreateProcess
        // search order (app dir/CWD before %SystemRoot%) — the interpreter
        // must be pinned to the OS copy.
        Assert.True(Path.IsPathRooted(path));
        Assert.StartsWith(
            Environment.SystemDirectory,
            path,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"),
            path,
            StringComparison.OrdinalIgnoreCase);
    }
}
