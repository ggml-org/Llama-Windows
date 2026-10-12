using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the Settings "Remove Llama Files" action's core. The install dir
/// is the SHARED WindowsApps folder — home of every packaged app's execution
/// aliases (winget.exe, python.exe, ...) — so the deletion must remove exactly
/// the names install.ps1 can leave there and never anything else. (The button
/// used to wipe the folder's entire contents, destroying other apps' aliases.)
/// </summary>
public sealed class ManagedInstallEmptyTests : IDisposable
{
    private readonly string _root;

    public ManagedInstallEmptyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "llama-install-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RemovesOnlyTheKnownLlamaArtifacts()
    {
        // Llama's files: the binary plus everything older installers dropped.
        foreach (var name in LlamaManager.ManagedInstallArtifacts)
            File.WriteAllText(Path.Combine(_root, name), "llama data");
        // Other apps' execution aliases — must survive untouched.
        File.WriteAllText(Path.Combine(_root, "winget.exe"), "foreign");
        File.WriteAllText(Path.Combine(_root, "python.exe"), "foreign");
        Directory.CreateDirectory(Path.Combine(_root, "OtherApp"));
        File.WriteAllText(Path.Combine(_root, "OtherApp", "tool.exe"), "foreign");

        var (deleted, failed) = LlamaManager.EmptyManagedInstall(_root);

        Assert.Equal(0, failed);
        Assert.Equal(LlamaManager.ManagedInstallArtifacts.Count, deleted);
        foreach (var name in LlamaManager.ManagedInstallArtifacts)
            Assert.False(File.Exists(Path.Combine(_root, name)), $"{name} should be gone");
        Assert.True(File.Exists(Path.Combine(_root, "winget.exe")), "foreign alias must survive");
        Assert.True(File.Exists(Path.Combine(_root, "python.exe")), "foreign alias must survive");
        Assert.True(File.Exists(Path.Combine(_root, "OtherApp", "tool.exe")), "foreign subdir must survive");
    }

    [Fact]
    public void EmptyDirectory_IsANoOp()
    {
        var (deleted, failed) = LlamaManager.EmptyManagedInstall(_root);

        Assert.Equal(0, deleted);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void ArtifactList_ContainsTheBinary_AndOnlyLlamaOwnedNames()
    {
        Assert.Contains("llama.exe", LlamaManager.ManagedInstallArtifacts);
        // Sanity: nothing generic in the list — every name is llama-owned so
        // the shared-folder deletion can never hit another app's file.
        foreach (var name in LlamaManager.ManagedInstallArtifacts)
        {
            Assert.True(
                name.StartsWith("llama.", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-probe.exe", StringComparison.OrdinalIgnoreCase) ||
                name is "unzstd.exe" or "featcode.exe" or "tmp.zst",
                $"unexpected non-llama artifact in the list: {name}");
        }
    }

    [Fact]
    public void ScratchDir_IsRemovedEntirely_WhenPresent()
    {
        var scratch = Path.Combine(_root, "llama-app");
        Directory.CreateDirectory(Path.Combine(scratch, "sub"));
        File.WriteAllText(Path.Combine(scratch, "tmp.zst"), "partial download");
        File.WriteAllText(Path.Combine(scratch, "sub", "probe.exe"), "probe");

        LlamaManager.DeleteInstallScratch(scratch);

        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public void ScratchDir_Missing_IsANoOp()
    {
        var scratch = Path.Combine(_root, "no-such-scratch");

        LlamaManager.DeleteInstallScratch(scratch); // must not throw

        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public void ScratchDir_IsTheLlamaAppFolderUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "llama-app");
        Assert.Equal(expected, LlamaManager.InstallScratchDir);
    }
}
