namespace LlamaApp.Common;

/// <summary>
/// The app's per-user data root: <c>%LOCALAPPDATA%\Llama</c>. Settings, logs,
/// and the managed-server PID file all live under it. Builds from before the
/// "LlamaApp" → "Llama" rename used <c>%LOCALAPPDATA%\LlamaApp</c>;
/// <see cref="MigrateLegacyFolder"/> moves that folder over on first launch
/// so existing settings and logs survive the rename.
/// </summary>
public static class AppData
{
    /// <summary>Root folder for all per-user app data (<c>%LOCALAPPDATA%\Llama</c>).</summary>
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Llama");

    private static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LlamaApp");

    /// <summary>
    /// Moves the pre-rename <c>LlamaApp</c> data folder to <see cref="Root"/>
    /// when the new folder doesn't exist yet. Best-effort and idempotent —
    /// call once at startup, before anything touches settings or logs.
    /// </summary>
    public static void MigrateLegacyFolder()
    {
        try
        {
            if (!Directory.Exists(Root) && Directory.Exists(LegacyRoot))
                Directory.Move(LegacyRoot, Root);
        }
        catch { /* best-effort — worst case the app starts with fresh settings */ }
    }
}
