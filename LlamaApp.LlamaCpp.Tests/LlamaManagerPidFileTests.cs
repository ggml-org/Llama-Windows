using System.Diagnostics;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the crash-safe managed-server check
/// (<see cref="LlamaManager.ReadLiveManagedPid"/>). The rules that matter
/// operationally: a live llama process whose PID matches the file IS our
/// managed server (adopt it after an app crash — don't kill it); a dead PID,
/// garbage content, or a live process that clearly isn't our server (PID
/// reuse) means "not managed" and the stale file is cleaned up.
/// </summary>
public sealed class LlamaManagerPidFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llama-pid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _spawned = new();

    public LlamaManagerPidFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string WritePidFile(string content)
    {
        var path = Path.Combine(_root, ".llama.pid");
        File.WriteAllText(path, content);
        return path;
    }

    private Process Spawn(string fileName, string arguments)
    {
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        _spawned.Add(p);
        return p;
    }

    /// <summary>Runs a cmd.exe copy named <c>llama.exe</c> so <see cref="Process.ProcessName"/> is "llama".</summary>
    private Process SpawnFakeLlama()
    {
        var fake = Path.Combine(_root, "llama.exe");
        if (!File.Exists(fake))
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), fake);
        return Spawn(fake, "/c ping -n 30 127.0.0.1 >nul");
    }

    [Fact]
    public void ReadLiveManagedPid_MissingFile_ReturnsNull()
    {
        Assert.Null(LlamaManager.ReadLiveManagedPid(Path.Combine(_root, ".llama.pid")));
    }

    [Fact]
    public void ReadLiveManagedPid_GarbageContent_ReturnsNull_AndDeletesFile()
    {
        var path = WritePidFile("not-a-pid");
        Assert.Null(LlamaManager.ReadLiveManagedPid(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadLiveManagedPid_DeadPid_ReturnsNull_AndDeletesFile()
    {
        var proc = Spawn("cmd.exe", "/c exit 0");
        proc.WaitForExit();

        var path = WritePidFile(proc.Id.ToString());
        Assert.Null(LlamaManager.ReadLiveManagedPid(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadLiveManagedPid_LiveNonLlamaProcess_ReturnsNull_AndDeletesFile()
    {
        // PID-reuse guard: a live process that isn't llama must not be
        // considered our managed server.
        var proc = Spawn("cmd.exe", "/c ping -n 30 127.0.0.1 >nul");
        var path = WritePidFile(proc.Id.ToString());

        Assert.Null(LlamaManager.ReadLiveManagedPid(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadLiveManagedPid_LiveLlamaProcess_ReturnsPid()
    {
        var proc = SpawnFakeLlama();
        var path = WritePidFile(proc.Id.ToString());

        Assert.Equal(proc.Id, LlamaManager.ReadLiveManagedPid(path));
    }

    [Fact]
    public void ReadLiveManagedPid_LlamaStartedAfterFileWrite_ReturnsNull_AndDeletesFile()
    {
        // PID-reuse guard #2: the process started AFTER the PID file's
        // timestamp — it can't be the server we launched.
        var proc = SpawnFakeLlama();
        var path = WritePidFile(proc.Id.ToString());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));

        Assert.Null(LlamaManager.ReadLiveManagedPid(path));
        Assert.False(File.Exists(path));
    }
}
