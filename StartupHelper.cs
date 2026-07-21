using System.IO;

namespace LlamaApp;

/// <summary>
/// Manages the "Launch at startup" behavior for LlamaApp by creating/removing
/// a <c>LlamaApp.lnk</c> shortcut in the user's per-user Startup folder
/// (<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</c>). This is the
/// reliable, MSIX-free way to autostart an unpackaged WinUI 3 app: the shell
/// runs everything in that folder on sign-in, and the user can see/toggle it
/// from Task Manager &gt; Startup apps (it shows up by the shortcut's name).
/// </summary>
/// <remarks>
/// The shortcut is built via the <c>WScript.Shell</c> COM host (late-bound), so
/// no extra NuGet reference is needed. <see cref="IsRegistered"/> is the
/// authoritative source of truth for the Settings checkbox — the user may have
/// disabled the entry from Task Manager without LlamaApp knowing.
/// </remarks>
public static class StartupHelper
{
    private const string ShortcutFileName = "LlamaApp.lnk";

    /// <summary>The full path of the startup shortcut we own.</summary>
    private static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ShortcutFileName);

    /// <summary>True if our startup shortcut currently exists on disk.</summary>
    public static bool IsRegistered() => File.Exists(ShortcutPath);

    /// <summary>
    /// Creates (or overwrites) the startup shortcut pointing at the running
    /// executable, so LlamaApp launches on the next sign-in.
    /// </summary>
    public static void Register()
    {
        var exe = GetExecutablePath();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new InvalidOperationException(
                "Could not resolve the running executable path for the startup shortcut.");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell instance.");
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            try
            {
                shortcut.TargetPath = exe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
                shortcut.WindowStyle = 1; // SW_SHOWNORMAL
                shortcut.IconLocation = exe + ",0";
                shortcut.Description = "Launch LlamaApp on sign-in";
                shortcut.Save();
            }
            finally
            {
                // Best-effort release of the COM object.
                if (shortcut is IDisposable d) d.Dispose();
            }
        }
        finally
        {
            if (shell is IDisposable sd) sd.Dispose();
        }
    }

    /// <summary>Removes the startup shortcut if present; no-op otherwise.</summary>
    public static void Unregister()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, "could not delete startup shortcut");
            throw;
        }
    }

    /// <summary>
    /// Resolves the path of the currently-running executable. Falls back to
    /// the main module path, which works for both packaged and unpackaged runs.
    /// </summary>
    private static string GetExecutablePath()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            var path = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch { /* fall through */ }
        return Environment.ProcessPath ?? "";
    }
}