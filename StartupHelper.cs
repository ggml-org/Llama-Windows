using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LlamaApp;

/// <summary>
/// Manages the "Launch at startup" behavior for Llama by creating/removing
/// a <c>Llama.lnk</c> shortcut in the user's per-user Startup folder
/// (<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</c>). This is the
/// reliable, MSIX-free way to autostart an unpackaged WinUI 3 app: the shell
/// runs everything in that folder on sign-in, and the user can see/toggle it
/// from Task Manager &gt; Startup apps (it shows up by the shortcut's name).
/// </summary>
/// <remarks>
/// The shortcut is built via static <c>IShellLinkW</c>/<c>IPersistFile</c> COM
/// interop (trim-safe — no reflection, no dynamic binder, so it survives
/// <c>PublishTrimmed</c> builds), so no extra NuGet reference is needed. <see
/// cref="IsRegistered"/> is the authoritative source of truth for the Settings
/// checkbox — the user may have disabled the entry from Task Manager without
/// Llama knowing.
/// </remarks>
public static class StartupHelper
{
    private const string ShortcutFileName = "Llama.lnk";

    // Pre-rename builds registered "LlamaApp.lnk". Kept so the rename doesn't
    // leave a dead duplicate behind in the Startup folder / Task Manager.
    private const string LegacyShortcutFileName = "LlamaApp.lnk";

    /// <summary>The full path of the startup shortcut we own.</summary>
    private static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ShortcutFileName);

    /// <summary>The full path of the pre-rename startup shortcut.</summary>
    private static string LegacyShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            LegacyShortcutFileName);

    /// <summary>True if our startup shortcut currently exists on disk.</summary>
    public static bool IsRegistered() => File.Exists(ShortcutPath);

    /// <summary>
    /// Creates (or overwrites) the startup shortcut pointing at the running
    /// executable, so Llama launches on the next sign-in.
    /// </summary>
    public static void Register()
    {
        RemoveLegacyShortcut();

        var exe = GetExecutablePath();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new InvalidOperationException(
                "Could not resolve the running executable path for the startup shortcut.");

        CreateShortcut(ShortcutPath, exe);
    }

    /// <summary>
    /// Writes a <c>.lnk</c> at <paramref name="shortcutPath"/> pointing at
    /// <paramref name="targetExe"/> (target, working directory, window style,
    /// icon and description). Static COM interop — the old WScript.Shell late
    /// binding went through the C# runtime binder, which trimming removes and
    /// would break exactly this path. Internal so tests can drive the COM
    /// round-trip against temp paths.
    /// </summary>
    internal static void CreateShortcut(string shortcutPath, string targetExe)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetExe);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetExe) ?? "");
            link.SetShowCmd(1); // SW_SHOWNORMAL
            link.SetIconLocation(targetExe, 0);
            link.SetDescription("Launch Llama on sign-in");
            ((IPersistFile)link).Save(shortcutPath, fRemember: true);
        }
        finally
        {
            // Best-effort release of the COM object.
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// Resolves the target of an existing <c>.lnk</c>, or null when the file
    /// isn't a readable shortcut. Internal so tests can verify the COM
    /// round-trip of <see cref="CreateShortcut"/>.
    /// </summary>
    internal static string? GetShortcutTarget(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0); // STGM_READ
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            return target.Length == 0 ? null : target;
        }
        catch (Exception ex) when (ex is COMException or IOException)
        {
            // Unreadable / missing / not-a-shortcut — the contract is null.
            // (A missing file surfaces as FileNotFoundException, an
            // IOException, from the COM marshaler — caught here too.)
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>Removes the startup shortcut if present; no-op otherwise.</summary>
    public static void Unregister()
    {
        RemoveLegacyShortcut();
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
    /// Deletes the pre-rename <c>LlamaApp.lnk</c> startup shortcut, if present.
    /// Best-effort; called once at startup and whenever the shortcut is
    /// (re)created or removed.
    /// </summary>
    public static void RemoveLegacyShortcut()
    {
        try
        {
            if (File.Exists(LegacyShortcutPath))
                File.Delete(LegacyShortcutPath);
        }
        catch { /* best-effort */ }
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

    // ---- Static COM interop for .lnk creation ----
    // Vtable order must match shobjidl_core.h exactly (InterfaceIsIUnknown is
    // positional); the tests' create/resolve round-trip pins the layout.

    /// <summary>CLSID_ShellLink — the .lnk coclass.</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    /// <summary>IShellLinkW (Unicode) — the shortcut's settings.</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMax, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMax);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMax);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMax);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchMax, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>IPersistFile — the Save/Load half of .lnk handling.</summary>
    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}