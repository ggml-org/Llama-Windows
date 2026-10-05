using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using LlamaApp.Common;

namespace LlamaApp;

/// <summary>
/// Anonymous usage telemetry, modeled on llama.cpp's text-generation-inference
/// <c>usage_stats.rs</c> event system (Start / Ping / Stop posted to a
/// Hugging Face endpoint). Scoped down for a desktop app: no prompt, model,
/// or file data ever leaves the machine — only the hardware summary below.
///
/// Flow: <see cref="StartAsync"/> at app launch sends one <c>start</c> event
/// with the hardware snapshot, a <c>ping</c> every 5 minutes while the process
/// is alive, and <see cref="SendStop"/> at exit sends a final <c>stop</c>
/// event. Every event carries a random, per-install <c>id</c> (persisted in
/// settings.json — NOT a hardware or account identifier) and
/// <c>app: "llamawin"</c>. Users can turn the whole thing off with the
/// <c>TelemetryEnabled</c> setting or, per-session, by setting the
/// <c>LLAMA_WINDOWS_TELEMETRY=OFF</c> environment variable.
/// </summary>
public static class Telemetry
{
    private const string Endpoint = "https://huggingface.co/api/telemetry/llamacpp";

    /// <summary>Env override: OFF (case-insensitive) disables, ON/unset/malformed falls through to the setting.</summary>
    private const string EnvVarName = "LLAMA_WINDOWS_TELEMETRY";

    /// <summary>Period between ping events while the app is running.</summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static bool _started;
    private static Timer? _pingTimer;
    private static string _id = "";
    private static TelemetryEnv? _env;

    /// <summary>
    /// Sends the start event and arms the ping timer. Fire-and-forget safe to
    /// call without awaiting (the app does so from <c>OnLaunched</c>); all
    /// failures — WMI probes, network, serialization — are swallowed and
    /// logged so telemetry can never interfere with startup.
    /// <para>Enabled when the <c>TelemetryEnabled</c> setting is on (the
    /// default) AND <c>LLAMA_WINDOWS_TELEMETRY</c> doesn't say OFF — an
    /// explicit <c>OFF</c> (case-insensitive) always wins; an explicit
    /// <c>ON</c>, an unset value, or a malformed value all fall through to
    /// the setting's verdict.</para>
    /// </summary>
    public static async Task StartAsync()
    {
        var envAllows = TelemetryEnvVar.Parse(
            Environment.GetEnvironmentVariable(EnvVarName));
        if (envAllows is false)
        {
            Log.Debug("telemetry disabled by LLAMA_WINDOWS_TELEMETRY=OFF");
            return;
        }
        if (!Settings.Current.TelemetryEnabled)
        {
            Log.Debug("telemetry disabled in settings; skipping");
            return;
        }
        if (_started) return;
        _started = true;

        try
        {
            _id = GetOrCreateClientId();
            // WMI probes can take a moment on first call — keep them off the
            // startup path. The snapshot is collected once and reused for
            // every event of the session.
            _env = await Task.Run(CollectEnvironment);

            await SendAsync(TelemetryEventType.Start);

            // Periodic ping: fires immediately after the interval, then every
            // interval, on a background thread. SendAsync never throws, so no
            // callback-level guard is needed.
            _pingTimer = new Timer(
                _ => _ = SendAsync(TelemetryEventType.Ping), null, PingInterval, PingInterval);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "telemetry start failed");
        }
    }

    /// <summary>
    /// Sends the stop event and tears the ping timer down. Synchronous (with
    /// a short cap) because the exit path goes straight to
    /// <c>Environment.Exit</c> — a fire-and-forget POST would be killed
    /// mid-flight. A stalled network must not hold the exit hostage, so the
    /// wait is capped well below the request timeout. No-op when telemetry is
    /// disabled or <see cref="StartAsync"/> never completed.
    /// </summary>
    public static void SendStop()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;

        if (!Settings.Current.TelemetryEnabled || !_started || _env is null) return;

        // Clear the started flag so a second call (settings toggle-off, then
        // the exit path) is a no-op instead of emitting a duplicate stop.
        _started = false;

        try
        {
            SendAsync(TelemetryEventType.Stop).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "telemetry stop failed");
        }
    }

    /// <summary>
    /// Builds and POSTs one event. Never throws: telemetry failures are
    /// logged at Debug and ignored, mirroring tgi's "don't block the service"
    /// stance. Returns regardless of delivery outcome.
    /// </summary>
    private static async Task SendAsync(TelemetryEventType eventType)
    {
        try
        {
            var payload = new TelemetryEvent
            {
                Id = _id,
                EventType = eventType,
                Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3),
                Env = _env ?? CollectEnvironment(),
            };
            var json = TelemetryJson.Serialize(payload);

            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Endpoint, content);
            Log.Debug($"telemetry {eventType.ToString().ToLowerInvariant()} → {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Debug($"telemetry {eventType.ToString().ToLowerInvariant()} send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The per-install client id: a random GUID generated once and persisted
    /// in settings.json. It is stable across launches (so the backend can
    /// tell installs apart) but random and unlinkable to anything else.
    /// Best-effort persistence — if the save fails the id is still used for
    /// this session and regenerated next launch.
    /// </summary>
    private static string GetOrCreateClientId()
    {
        var id = Settings.Current.TelemetryId;
        if (!string.IsNullOrWhiteSpace(id)) return id;

        id = Guid.NewGuid().ToString();
        Settings.Current.TelemetryId = id;
        Settings.Current.Save();
        return id;
    }

    /// <summary>
    /// Collects the hardware snapshot: Windows version, CPU name and logical
    /// count, total RAM, and the installed GPU names (empty list when none).
    /// Every probe is individually guarded — a failed probe yields a default
    /// value rather than aborting the event.
    /// </summary>
    private static TelemetryEnv CollectEnvironment()
    {
        return new TelemetryEnv
        {
            OsName = "windows",
            OsVersion = TryOsVersion(),
            Cpu = TryCpuName(),
            CpuCount = Environment.ProcessorCount,
            MemoryTotal = TryTotalMemory(),
            Gpu = TryGpuNames(),
        };
    }

    /// <summary>Windows version as <c>major.minor.build</c> (e.g. 10.0.26100).</summary>
    private static string TryOsVersion()
    {
        try
        {
            var v = Environment.OSVersion.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return ""; }
    }

    /// <summary>CPU model string from WMI (e.g. "AMD Ryzen 9 7950X").</summary>
    private static string TryCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var item in searcher.Get())
            {
                var name = item["Name"] as string;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch (Exception ex) { Log.Debug($"cpu probe failed: {ex.Message}"); }
        return "";
    }

    /// <summary>
    /// Total physical RAM in bytes — the same <c>GlobalMemoryStatusEx</c> probe
    /// as <c>Llama.SystemMemory</c>, duplicated here because Common must not
    /// depend on the llama.cpp layer. Returns 0 when the OS call fails.
    /// </summary>
    private static ulong TryTotalMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status)) return status.ullTotalPhys;
        }
        catch (Exception ex) { Log.Debug($"memory probe failed: {ex.Message}"); }
        return 0;
    }

    /// <summary>
    /// Names of the installed video controllers via WMI, deduplicated in
    /// device order (e.g. "NVIDIA GeForce RTX 4060 Ti"). Empty when the probe
    /// fails or no GPU is present.
    /// </summary>
    private static string[] TryGpuNames()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var names = new List<string>();
            foreach (var item in searcher.Get())
            {
                var name = item["Name"] as string;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
            return names.Distinct().ToArray();
        }
        catch (Exception ex) { Log.Debug($"gpu probe failed: {ex.Message}"); }
        return [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

