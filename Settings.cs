using System.IO;
using System.Text.Json;

namespace LlamaApp;

/// <summary>
/// User-configured application settings, persisted as JSON in the app's
/// per-user local data folder. Holds the Hugging Face access token (for
/// authenticated downloads / private repos) and the local models cache
/// directory (where GGUF files live, shared with the HF cache layout).
/// </summary>
public sealed class Settings
{
    /// <summary>Singleton instance; loaded lazily on first access and cached.</summary>
    public static Settings Current { get; } = Load();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LlamaApp", "settings.json");

    static Settings()
    {
        // Make sure the directory exists so Save() never throws on a missing dir.
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Hugging Face access token (hf_…). Optional — only needed for downloading
    /// private/gated repos. Stored in the local settings file (per-user, not
    /// roamed); leave empty for anonymous access to public repos.
    /// </summary>
    public string HuggingFaceToken { get; set; } = "";

    /// <summary>
    /// Local directory where downloaded GGUF models are cached. Defaults to the
    /// standard Hugging Face cache (<c>%USERPROFILE%\.cache\huggingface\hub</c>)
    /// so models are shared with <c>llama.cpp</c> and other HF-aware tools.
    /// </summary>
    public string CacheDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "huggingface", "hub");

    /// <summary>
    /// Whether LlamaApp should launch automatically when the user signs in to
    /// Windows. The authoritative state is the presence of the startup
    /// shortcut managed by <see cref="StartupHelper"/> (in the user's Startup
    /// folder); this value is a persisted hint so the Settings checkbox can
    /// reflect intent on first open before re-reading the OS state.
    /// </summary>
    public bool LaunchAtStartup { get; set; } = false;

    private static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable settings — fall back to defaults rather than
            // crashing the app. The user can re-enter values in the Settings UI.
            Common.Log.Warn(ex, "settings load failed; using defaults");
        }
        return new Settings();
    }

    /// <summary>
    /// Persists the current values to <c>settings.json</c>. Best-effort: a
    /// failure (e.g. disk full) is swallowed and returns false rather than
    /// surfacing in the UI flow.
    /// </summary>
    public bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(SettingsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, "settings save failed");
            return false;
        }
    }
}