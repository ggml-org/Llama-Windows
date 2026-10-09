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
    /// IPv4 address the llama server binds to (<c>--host</c>): the loopback
    /// <c>127.0.0.1</c> (the default, reachable only from this machine),
    /// <c>0.0.0.0</c> for every interface, or a specific interface's address
    /// from Settings → Llama. Read once at startup when the
    /// <see cref="Llama.LlamaManager"/> singleton is created, so a changed
    /// value takes effect on the next app launch — the app's own REST client
    /// follows the same address (loopback when the server binds all
    /// interfaces).
    /// </summary>
    public string ListenAddress { get; set; } = LlamaApp.Common.ListenAddresses.Localhost;

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
    /// Maximum number of models the llama server loads simultaneously in
    /// router mode: 0 (the default) means unlimited, any positive value caps
    /// how many per-model child servers run at once. Handed to the server as
    /// <c>--models-max</c> at launch (see
    /// <see cref="Llama.LlamaManager.MaxLoadedModels"/>), so a changed value
    /// takes effect on the next server start.
    /// </summary>
    public int MaxLoadedModels { get; set; } = 0;

    /// <summary>
    /// KV cache quantization type for the key tensor, handed to the server as
    /// <c>--cache-type-k</c> at launch. One of <c>f32</c>, <c>f16</c>,
    /// <c>bf16</c>, <c>q8_0</c>, <c>q4_0</c>, <c>q4_1</c>, <c>iq4_nl</c>,
    /// <c>q5_0</c>, or <c>q5_1</c>. Defaults to <c>f16</c>, llama.cpp's own
    /// default; lower-precision types shrink the KV cache so a larger context
    /// fits in GPU memory. A changed value takes effect on the next server
    /// start.
    /// </summary>
    public string CacheTypeK { get; set; } = "f16";

    /// <summary>
    /// KV cache quantization type for the value tensor, handed to the server
    /// as <c>--cache-type-v</c> at launch. Same value set and default as
    /// <see cref="CacheTypeK"/>. K and V can be quantized independently.
    /// </summary>
    public string CacheTypeV { get; set; } = "f16";

    /// <summary>
    /// Free-form extra <c>llama serve</c> arguments, appended after the
    /// built-in flags so they can override them (llama.cpp honors the last
    /// occurrence). Stored exactly as typed — including newlines, which the
    /// tokenizer treats as whitespace — and split into argv tokens at launch
    /// (see <see cref="Common.ArgumentTokenizer.Tokenize"/>). Applies the
    /// next time the llama server starts; a bad flag makes the server exit
    /// with an error, which the app surfaces as the failure reason.
    /// </summary>
    public string CustomServeArguments { get; set; } = "";

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

    /// <summary>
    /// Per-model context-length preferences chosen in the model details view,
    /// keyed by the server model id (<c>repo:quant</c>) — context length is a
    /// per-model choice, not a global one (a 1B model and a 70B model want very
    /// different defaults). Applied as <c>ctx_size</c> on the next
    /// <c>/models/load</c> (see <see cref="Llama.LlamaManager.LoadModelAsync"/>).
    /// </summary>
    public Dictionary<string, int> ModelContextLengths { get; set; } = new();

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