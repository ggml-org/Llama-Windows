using LlamaApp;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the startup-shortcut COM round-trip
/// (<see cref="StartupHelper.CreateShortcut"/> /
/// <see cref="StartupHelper.GetShortcutTarget"/>). The .lnk is written through
/// static IShellLinkW/IPersistFile interop whose vtable layout is positional —
/// this create/resolve round-trip pins it (the old WScript.Shell late binding
/// is dynamic-binder code and breaks trimmed builds). Runs against temp paths,
/// never the real Startup folder.
/// </summary>
public class StartupHelperShortcutTests
{
    [Fact]
    public void Create_And_Resolve_Round_Trips_The_Target()
    {
        var lnk = Path.Combine(Path.GetTempPath(), $"llama-test-{Guid.NewGuid():N}.lnk");
        try
        {
            StartupHelper.CreateShortcut(lnk, Environment.ProcessPath!);

            Assert.True(File.Exists(lnk));
            Assert.Equal(Environment.ProcessPath, StartupHelper.GetShortcutTarget(lnk));
        }
        finally
        {
            File.Delete(lnk);
        }
    }

    [Fact]
    public void Resolving_A_Missing_Shortcut_Yields_Null()
        => Assert.Null(StartupHelper.GetShortcutTarget(
            Path.Combine(Path.GetTempPath(), $"llama-missing-{Guid.NewGuid():N}.lnk")));
}
