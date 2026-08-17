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
    // Declared BEFORE <c>Current</c> so its static field initializer runs first.
    // Static field initializers run in textual order, and <c>Current</c>'s
    // initializer calls <see cref="Load"/> — if SettingsPath were declared
    // below it, Load would see <c>SettingsPath == null</c> (still its default),
    // <see cref="File.Exists"/> would return false, and every saved setting
    // (HuggingFace token, cache directory, startup hint) would be silently
    // discarded on every launch. Keep this above any member that calls Load.
    private static readonly string SettingsPath = Path.Combine(
        Common.AppData.Root, "settings.json");

    /// <summary>Singleton instance; loaded lazily on first access and cached.</summary>
    public static Settings Current { get; } = Load();

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
    /// Port the local llama server listens on (default 9931). Read once at
    /// startup when the <see cref="Llama.LlamaManager"/> singleton is created
    /// (App.OnLaunched), so a changed value takes effect on the next app
    /// launch. Valid range: 1–65535; out-of-range values fall back to the
    /// default at startup.
    /// </summary>
    public int ServerPort { get; set; } = Llama.LlamaManager.DefaultServerPort;

    /// <summary>
    /// Seconds of idleness after which the llama server unloads the model from
    /// memory: 300 (5 min), 900 (15 min), 3600 (1 hour), or -1 (never, the
    /// default). Handed to the server as <c>--sleep-idle-seconds</c> at launch
    /// (see <see cref="Llama.LlamaManager.IdleUnloadSeconds"/>), so a changed
    /// value takes effect on the next server start. An idled-out model stays
    /// listed and wakes transparently on the next request.
    /// </summary>
    public int IdleUnloadSeconds { get; set; } = -1;

    /// <summary>
    /// Whether Llama should launch automatically when the user signs in to
    /// Windows. The authoritative state is the presence of the startup
    /// shortcut managed by <see cref="StartupHelper"/> (in the user's Startup
    /// folder); this value is a persisted hint so the Settings checkbox can
    /// reflect intent on first open before re-reading the OS state.
    /// </summary>
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>
    /// Whether the one-time first-run hint ("Llama lives in the system
    /// tray; Alt+Space opens the chat overlay") has been shown. Persisted so
    /// the toast fires exactly once, on the first launch.
    /// </summary>
    public bool TrayHintShown { get; set; } = false;

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