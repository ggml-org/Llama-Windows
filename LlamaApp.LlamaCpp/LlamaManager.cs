using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// Manages the local <c>llama</c> (llama.cpp) executable: detects an existing
/// installation, and downloads + runs the official
/// <a href="https://llama.app/install.ps1">install.ps1</a> when none is found.
///
/// <para>Mirrors the macOS app's <c>LlamaInstallManager</c> + <c>LlamaBinaries</c>:
/// <c>install.ps1</c> puts <c>llama</c> on the user PATH (its install dir,
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, is there by default), so the app
/// never hardcodes the binary location — it resolves <c>llama.exe</c> with a
/// <c>which</c>-style PATH lookup (<see cref="FindOnPath(string)"/>). A hit under
/// the install dir is the app-managed installation (may be installed/emptied);
/// a hit anywhere else is the user's own external installation and is left alone.
/// The installation is silent (writes under the user profile, no elevation needed).</para>
///
/// <para>Call <see cref="EnsureLlamaOrDownloadAsync"/> at startup; it adopts a
/// running server, launches one, or downloads the binary on demand, and reports
/// progress/state via <see cref="StateChanged"/>. Once the server is reachable,
/// <see cref="GetModelsAsync"/> lists locally available models via the
/// <c>GET /models</c> REST endpoint.
/// </para>
///
/// <para>A server the app starts is <b>managed</b>: its PID is written to
/// <c>%LOCALAPPDATA%\Llama\.llama.pid</c> right after spawn, so that after
/// an app crash the next instance still recognizes the surviving server as its
/// own — and kills it on exit (<see cref="StopServer"/>). Servers started any
/// other way (manually, whatever the binary) have no PID file and are left
/// alone.</para>
/// </summary>
public sealed class LlamaManager
{
    private static LlamaManager? _shared;

    /// <summary>
    /// Shared singleton, matching the macOS app's <c>.shared</c>. Created by
    /// <see cref="Initialize"/> with the configured server port — the app calls
    /// it once at startup (App.OnLaunched) before anything else can touch the
    /// manager (the MainWindow constructor subscribes to its events).
    /// </summary>
    public static LlamaManager Shared =>
        _shared ?? throw new InvalidOperationException(
            "LlamaManager.Initialize(serverPort) must be called once at startup before first use.");

    /// <summary>
    /// Creates the <see cref="Shared"/> singleton bound to
    /// <paramref name="serverPort"/> and <paramref name="listenAddress"/>.
    /// Must be called once at startup, before the first
    /// <see cref="Shared"/> access: both are baked in at construction and
    /// every health probe / launch argument / REST URL derives from them, so
    /// changed settings only take effect on the next app launch. A null,
    /// blank, or unparseable address falls back to loopback. When
    /// <paramref name="apiKey"/> is non-empty it is sent as a Bearer token on
    /// every request and passed to the server via a per-user key file
    /// (<c>--api-key-file</c>).
    /// </summary>
    public static LlamaManager Initialize(int serverPort, string? listenAddress = null, string? apiKey = null)
    {
        if (_shared is not null)
            throw new InvalidOperationException("LlamaManager is already initialized.");
        if (serverPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(serverPort), "Port must be in 1..65535.");
        if (!System.Net.IPAddress.TryParse(listenAddress, out var parsed))
            listenAddress = Common.ListenAddresses.Localhost;
        else if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            listenAddress = Common.ListenAddresses.Localhost; // IPv4 only — matches the server's bind.
        _shared = new LlamaManager(serverPort, listenAddress, apiKey);
        return _shared;
    }

    /// <summary>URL of the official Windows install script.</summary>
    private static readonly Uri InstallScriptUrl = new(InstallScriptIntegrity.ExpectedScriptUrl);

    /// <summary>
    /// The install dir <c>install.ps1</c> targets — on the user PATH by default,
    /// which is how the script makes <c>llama</c> resolvable. Never probed
    /// directly (the binary is discovered via <see cref="FindOnPath(string)"/>);
    /// used to classify a PATH hit as managed vs external
    /// (<see cref="IsManagedPath"/>) and to back the Settings "Installation
    /// Folder" card.
    /// </summary>
    private static string ManagedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");

    /// <summary>
    /// The install directory the app manages — surfaced read-only in Settings
    /// ("Installation Folder" card). External (PATH) installations are never
    /// managed: their location comes from <see cref="BinaryPath"/> instead.
    /// </summary>
    public static string ManagedInstallDir => ManagedDir;

    /// <summary>
    /// Where the resolved binary comes from — surfaced in the flyout footer, so
    /// an external installation isn't mistaken for the app's own (and a stale
    /// version isn't mistaken for a bug).
    /// </summary>
    public enum Origin
    {
        /// <summary>Not yet resolved — no binary found and no install attempted.</summary>
        Unknown,
        /// <summary>App-managed binary found in <see cref="ManagedInstallDir"/> (what <c>install.ps1</c> produces).</summary>
        Managed,
        /// <summary>Pre-existing installation found on PATH (not modified by the app).</summary>
        External,
    }

    /// <summary>Install lifecycle state, surfaced in the UI.</summary>
    public enum InstallState
    {
        /// <summary>Ready — a usable binary is present (or we haven't needed to act).</summary>
        Idle,
        /// <summary>Downloading/installing the app-managed binary.</summary>
        Installing,
        /// <summary>The installation failed; retry via <see cref="EnsureLlamaOrDownloadAsync"/>.</summary>
        Failed,
    }

    /// <summary>Server lifecycle state surfaced in the UI.</summary>
    public enum ServerState
    {
        /// <summary>Not started (no binary yet, or stopped).</summary>
        Stopped,
        /// <summary>Launched; waiting for the port to respond.</summary>
        Starting,
        /// <summary>Listening and serving requests.</summary>
        Running,
        /// <summary>The process exited unexpectedly or failed to bind.</summary>
        Failed,
    }

    /// <summary>
    /// Default port the local llama server listens on when the user hasn't
    /// configured one (mirrored by <c>Settings.ServerPort</c> in the app project).
    /// </summary>
    public const int DefaultServerPort = 9931;

    /// <summary>
    /// KV cache quantization types accepted as <c>--cache-type-k</c>/
    /// <c>--cache-type-v</c> values. Kept in one place so a hand-edited
    /// settings.json with an unrecognized value never reaches the server as an
    /// invalid flag.
    /// </summary>
    internal static readonly string[] SupportedKvCacheTypes =
    {
        "f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1",
    };

    /// <summary>
    /// Port the local llama server listens on (matches the flyout link). Fixed
    /// at construction via <see cref="Initialize"/> — the supervisor loop,
    /// health probes, server launch arguments and every REST URL are built
    /// from it.
    /// </summary>
    public int ServerPort { get; }

    /// <summary>
    /// IPv4 address the llama server binds to via <c>--host</c>: the loopback
    /// <c>127.0.0.1</c> (the default), <c>0.0.0.0</c> for every interface, or
    /// a specific interface's address. Fixed at construction via
    /// <see cref="Initialize"/>, like the port — the app's own REST client
    /// follows it (loopback when the server binds all interfaces, so the
    /// client and the bind address never disagree). A changed value takes
    /// effect on the next app launch.
    /// </summary>
    public string ListenAddress { get; }

    /// <summary>
    /// The address the app's own REST client and user-facing URLs connect to:
    /// the <see cref="ListenAddress"/> itself, except loopback when the
    /// server binds all interfaces (<c>0.0.0.0</c> is not a connectable
    /// destination). Kept separate from <see cref="ListenAddress"/> so the
    /// bind flag and the client never disagree.
    /// </summary>
    public string ConnectAddress { get; }

    /// <summary>
    /// API key the app generated for a non-loopback bind, or null for the
    /// default loopback server (which needs none). Sent as a Bearer token on
    /// every REST request and handed to the server via a per-user key file
    /// (<c>--api-key-file</c> — never an argv flag, which any local user could
    /// read via WMI), so a network-exposed server isn't an open control API.
    /// It must never appear in URLs (browser/WebView history, caches, server
    /// logs): the overlay injects it as a request header, the sample curl uses
    /// the Authorization header, and Settings shows it for copy if the
    /// server's WebUI ever asks for it.
    /// </summary>
    public string? ApiKey { get; }

    /// <summary>
    /// The address the app's REST client connects to for a given bind address:
    /// loopback when the server binds all interfaces (<c>0.0.0.0</c> is not a
    /// connectable destination), the address itself otherwise. Pure so the
    /// mapping is unit-testable.
    /// </summary>
    internal static string ConnectAddressFor(string listenAddress)
        => listenAddress == Common.ListenAddresses.AllInterfaces
            ? Common.ListenAddresses.Localhost
            : listenAddress;

    /// <summary>
    /// Hugging Face cache directory passed to the server via
    /// <c>HF_HUB_CACHE</c> so it resolves downloaded models from the same
    /// location the app scans. Set by the caller (App.OnLaunched reads it from
    /// <c>Settings.Current.CacheDirectory</c>) — kept here rather than reading
    /// <c>Settings</c> directly to avoid a circular project dependency.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Optional recursive sources of existing GGUF files, separate from the HF cache.</summary>
    public IReadOnlyList<string>? AdditionalModelDirectories { get; set; }

    private readonly LocalModelDirectoryCache _additionalModels = new();

    /// <summary>
    /// Hugging Face access token passed to the server via <c>HF_TOKEN</c> so
    /// it can pull private/gated models on the user's behalf (llama.cpp reads
    /// the variable and sends it as a Bearer token on Hub requests). Set by
    /// the caller (App.OnLaunched reads it from
    /// <c>Settings.Current.HuggingFaceToken</c>) — kept here rather than
    /// reading <c>Settings</c> directly to avoid a circular project
    /// dependency. Only affects servers the app launches: an adopted
    /// already-running server keeps whatever environment it was started with.
    /// Never logged — presence only.
    /// </summary>
    public string? HuggingFaceToken { get; set; }

    /// <summary>
    /// Seconds of idleness after which the server unloads a model from memory,
    /// passed as <c>--sleep-idle-seconds</c> at launch; -1 (the default)
    /// disables it. An idled-out model reports <c>sleeping</c> — it stays the
    /// active model and wakes transparently on the next request. Set by the
    /// caller (App.OnLaunched reads it from
    /// <c>Settings.Current.IdleUnloadSeconds</c>) — kept here rather than
    /// reading <c>Settings</c> directly to avoid a circular project
    /// dependency. Only affects servers the app launches: an adopted
    /// already-running server keeps whatever arguments it was started with,
    /// and a changed value takes effect on the next server start.
    /// </summary>
    public int IdleUnloadSeconds { get; set; } = -1;

    /// <summary>
    /// Maximum number of models loaded in parallel in router mode, passed as
    /// <c>--models-max</c> at launch; 0 (the default) means unlimited. Set by
    /// the caller (App.OnLaunched reads it from
    /// <c>Settings.Current.MaxLoadedModels</c>) — kept here rather than reading
    /// <c>Settings</c> directly to avoid a circular project dependency. Only
    /// affects servers the app launches: an adopted already-running server
    /// keeps whatever arguments it was started with, and a changed value takes
    /// effect on the next server start.
    /// </summary>
    public int MaxLoadedModels { get; set; } = 0;

    /// <summary>
    /// KV cache quantization type for the key tensor, passed as
    /// <c>--cache-type-k</c> at launch; defaults to <c>f16</c>, llama.cpp's
    /// own default. Supported values are <c>f32</c>, <c>f16</c>, <c>bf16</c>,
    /// <c>q8_0</c>, <c>q4_0</c>, <c>q4_1</c>, <c>iq4_nl</c>, <c>q5_0</c>, and
    /// <c>q5_1</c>. Set by the caller (App.OnLaunched reads it from
    /// <c>Settings.Current.CacheTypeK</c>) — kept here rather than reading
    /// <c>Settings</c> directly to avoid a circular project dependency. Only
    /// affects servers the app launches: an adopted already-running server
    /// keeps whatever arguments it was started with, and a changed value takes
    /// effect on the next server start.
    /// </summary>
    public string CacheTypeK { get; set; } = "f16";

    /// <summary>
    /// KV cache quantization type for the value tensor, passed as
    /// <c>--cache-type-v</c> at launch. Same supported values and default as
    /// <see cref="CacheTypeK"/>; K and V may be quantized independently.
    /// </summary>
    public string CacheTypeV { get; set; } = "f16";

    /// <summary>
    /// Free-form extra <c>llama serve</c> arguments from the Settings page,
    /// tokenized at launch (<see cref="Common.ArgumentTokenizer.Tokenize"/>)
    /// and appended AFTER the built-in flags so they can override them.
    /// Set by the caller (App.OnLaunched reads it from
    /// <c>Settings.Current.CustomServeArguments</c>) — kept here rather than
    /// reading <c>Settings</c> directly to avoid a circular project dependency.
    /// Only affects servers the app launches: an adopted already-running
    /// server keeps whatever arguments it was started with, and a changed
    /// value takes effect on the next server start.
    /// </summary>
    public string? CustomServeArguments { get; set; }

    /// <summary>Preferred app-managed llama.cpp backend; automatic preserves the installer default.</summary>
    public string RuntimeBackend { get; set; } = InferenceRuntime.Automatic;

    /// <summary>Saved GPU choice from Settings, or empty for automatic device selection.</summary>
    public string? GpuDeviceName { get; set; }

    /// <summary>"auto", "all", or a numeric maximum for GPU layer offload.</summary>
    public string? GpuLayers { get; set; } = "auto";

    /// <summary>Optional logical prompt batch size; 0 keeps the server default.</summary>
    public int BatchSize { get; set; }

    /// <summary>Optional physical prompt microbatch size; 0 keeps the server default.</summary>
    public int MicroBatchSize { get; set; }

    /// <summary>Flash Attention mode: auto, on, or off.</summary>
    public string? FlashAttention { get; set; } = "auto";

    /// <summary>Additional advanced defaults applied to model child processes.</summary>
    public ModelPromptProcessingProfile AdvancedInferenceProfile { get; set; } = new();

    private DeviceProbe _lastDeviceProbe = new(false, []);
    private ServeCapabilities _serveCapabilities = new(false, false, false, false);

    private ModelPromptProcessingProfile BuildGlobalInferenceProfile() =>
        (AdvancedInferenceProfile ?? new()) with
        {
            BatchSize = BatchSize,
            MicroBatchSize = MicroBatchSize,
            FlashAttention = FlashAttention ?? "auto",
            GpuDeviceName = "",
            GpuLayers = GpuLayers is null or "auto" ? "" : GpuLayers,
            CacheTypeK = CacheTypeK == "f16" ? "" : CacheTypeK ?? "",
            CacheTypeV = CacheTypeV == "f16" ? "" : CacheTypeV ?? "",
        };

    /// <summary>Resolved device passed to the managed server, if explicitly selected.</summary>
    public string? ActiveDeviceId { get; private set; }

    /// <summary>Why a selected accelerator fell back to CPU, if applicable.</summary>
    public string? RuntimeFallbackReason { get; private set; }

    /// <summary>Saved model overrides omitted because they cannot be used by this runtime.</summary>
    public string? ModelProfileWarning
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string AppliedRuntimePath => Path.Combine(AppData.Root, "runtime-backend.txt");

    private static string ReadAppliedRuntime()
    {
        try { return File.ReadAllText(AppliedRuntimePath).Trim(); }
        catch { return InferenceRuntime.Automatic; } // Older installs used automatic selection.
    }

    private static void WriteAppliedRuntime(string backend)
    {
        try
        {
            Directory.CreateDirectory(AppData.Root);
            File.WriteAllText(AppliedRuntimePath, InferenceRuntime.Normalize(backend));
        }
        catch (Exception ex) { Log.Warn(ex, "could not record installed llama backend"); }
    }

    /// <summary>
    /// Per-model context-length preferences (keyed by server model id,
    /// <c>repo:quant</c>), rendered into a llama.cpp <c>--models-preset</c> INI
    /// at server launch so the router spawns each child with its chosen
    /// <c>--ctx-size</c>. This INI is the only channel the router honors: its
    /// <c>/models/load</c> endpoint reads only the model name and silently
    /// ignores a ctx field in the request body (verified against the llama.cpp
    /// source and empirically). Set by the caller (App.OnLaunched shares
    /// <c>Settings.Current.ModelContextLengths</c> by reference, so later edits
    /// are visible here) — kept here rather than reading <c>Settings</c>
    /// directly to avoid a circular project dependency. Only affects servers
    /// the app launches; an adopted already-running server keeps whatever it
    /// was started with.
    /// </summary>
    public IReadOnlyDictionary<string, int>? ModelContextLengths { get; set; }

    /// <summary>Per-model prompt-processing overrides rendered into --models-preset.</summary>
    public IReadOnlyDictionary<string, ModelPromptProcessingProfile>? ModelPromptProfiles { get; set; }

    private Process? _serverProcess;

    // Single-flight guard for EnsureLlamaOrDownloadAsync / StartServerAsync. Called
    // fire-and-forget from App.OnLaunched and re-entrant via StateChanged
    // handlers; without it, two concurrent callers can both pass the initial
    // "no server reachable" probe and both spawn a `llama serve --port 2276`,
    // leaking processes (one fails to bind and may linger; the second binds and eats
    // RAM). The gate serializes launches within one process; cross-instance
    // races are handled by the retrying adoption probe (see WaitForReachableAsync).
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    /// <summary>
    /// The single <see cref="HttpClient"/> for every llama-server REST call.
    /// <see cref="HttpClient.BaseAddress"/> carries the configured port and
    /// the selected listen address — loopback (<c>127.0.0.1</c>) whenever the
    /// server binds all interfaces, so the client always has an address that
    /// can actually be connected to. The handler bypasses the system proxy: a
    /// configured proxy/VPN must never intercept local traffic (the classic
    /// cause of "browser gets 200 OK, HttpClient fails"). The client-level
    /// timeout is infinite; each call bounds itself with a linked token
    /// (<see cref="WithTimeout"/>) so SSE streams can run unbounded while
    /// probes stay snappy.
    /// </summary>
    private readonly HttpClient _http;

    /// <summary>Current installation state.</summary>
    public InstallState State
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    } = InstallState.Idle;

    /// <summary>User-facing reason for the <see cref="ServerState.Failed"/> state, if any.</summary>
    public string? FailureMessage
    {
        get;
        private set
        {
            field = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Path to the resolved <c>llama.exe</c>, or <c>null</c> if none.</summary>
    public string? BinaryPath
    {
        get;
        private set
        {
            field = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Version string reported by the resolved binary, or <c>null</c>.</summary>
    public string? Version
    {
        get;
        private set
        {
            field = value;
            // Keep LlamaRunner.Version in sync so the footer reads live.
            LlamaRunner.VersionCache = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Where the resolved binary comes from.</summary>
    public Origin CurrentOrigin
    {
        get;
        private set
        {
            field = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    } = Origin.Unknown;

    /// <summary>
    /// Current server state — derived from HTTP API polls by the always-on
    /// supervisor (see <see cref="DeriveServerStatus"/>), never from process
    /// handles: a spawned server's crash and an adopted server's crash look
    /// identical to the poll, and a server that appears is adopted the same
    /// way no matter who started it.
    /// </summary>
    public ServerState ServerStatus
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    } = ServerState.Stopped;

    /// <summary>Raised whenever any observable property changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised by the supervisor loop (see <see cref="SupervisorLoopAsync"/>) on a
    /// background thread roughly every 500ms with a fresh <c>GET /models</c>
    /// snapshot while the server is <see cref="ServerState.Running"/>.
    /// Handlers should marshal to the UI thread before touching view models.
    /// </summary>
    public event EventHandler<IReadOnlyList<ServerModel>>? ModelsChanged;

    private LlamaManager(int serverPort, string listenAddress, string? apiKey)
    {
        ServerPort = serverPort;
        ListenAddress = listenAddress;
        // The app's REST client follows the bind address, except that
        // 0.0.0.0 means "the server listens on every interface" — for the
        // client, pick loopback, the one address that is always among them.
        var connectAddress = ConnectAddressFor(listenAddress);
        ConnectAddress = connectAddress;
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://{connectAddress}:{serverPort}"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // Same User-Agent as the internet-facing clients — the loopback
        // server logs then identify the caller consistently.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent.Value);
        // A non-loopback server requires an API key; set it once here so every
        // probe, poll, SSE stream and chat request carries it.
        if (ApiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        // The supervisor is the ONLY source of truth for server status: it
        // polls the HTTP API for the app's whole lifetime and derives
        // ServerStatus from the answers — no process-handle assumptions.
        // Fire-and-forget: the loop is inert while Stopped and every tick is
        // guarded, so it can't fault the process.
        _ = Task.Run(SupervisorLoopAsync);
    }

    /// <summary>
    /// Ensures a llama server is reachable at
    /// <c><see cref="ConnectAddress"/>:<see cref="ServerPort"/></c> — the app's
    /// single point of contact for the model REST API. Resolution order:
    /// <list type="number">
    /// <item><b>Probe</b> <c>GET /health</c>. If a server is already running (a
    /// previous app instance, another tool, or a manual launch), adopt it as the
    /// client — no binary needed, no process launched.</item>
    /// <item>Otherwise <b>resolve</b> the <c>llama</c> binary (app-managed or on
    /// PATH) and <see cref="StartServerAsync">launch it</see>.</item>
    /// <item>If no binary is found, <b>download</b> it via the official
    /// <c>install.ps1</c> (see <see cref="InstallAsync"/>), then launch the server.</item>
    /// </list>
    /// Returns <c>true</c> once the server is reachable. The Available models list
    /// is then fetched via <see cref="GetModelsAsync"/>. Safe to await from the UI
    /// thread; installs run on a background process.
    /// </summary>
    public async Task<bool> EnsureLlamaOrDownloadAsync(CancellationToken cancel = default)
    {
        // Automatic and external installations may also resolve to a ROCm
        // binary. Its native imports must be reachable before any child starts.
        HipSdkEnvironment.EnsureOnProcessPath();
        // Single-flight: a prior or concurrent caller may already be bringing
        // the server up (or about to). Waiting here means the second caller
        // finds Running after the first releases the gate — no duplicate spawn.
        await _ensureGate.WaitAsync(cancel);
        try
        {
            // Re-check after acquiring: a prior caller just brought the server up.
            if (ServerStatus == ServerState.Running)
            {
                Log.Info("llama server already running (gate re-check)");
                return true;
            }

            // 1. Adopt an already-running server (no binary/process needed).
            // Probe briefly (a few attempts over ~3s) rather than once: a sibling
            // app instance / a manual launch / a server that's just binding won't
            // answer the very first probe, and a single has misused to spawn a
            // SECOND `llama serve` on the same port here — leaving two processes eating
            // RAM (the loser fails to bind, but the app would also abandon timed-
            // out launches alive — see StartServerAsync). A short adoption window
            // catches the in-flight server and adopts it instead.
            if (await WaitForReachableAsync(TimeSpan.FromSeconds(3), cancel))
            {
                Log.Info("adopted an already-running llama server");
                ServerStatus = ServerState.Running;
                // Best-effort: resolve the binary so Version is populated for display,
                // but don't block the client on it.
                _ = ResolveAndReadVersionAsync(cancel);
                return true;
            }

            // 2/3. Resolve the binary; install if missing; then launch the server.
            var resolved = Resolve();
            Log.Info($"resolved llama binary: kind={resolved.Kind} path={resolved.Path ?? "<none>"}");
            switch (resolved.Kind)
            {
                case ResolutionKind.Managed:
                    var needsManagedInstall = InferenceRuntime.RequiresManagedInstall(
                        RuntimeBackend, ReadAppliedRuntime());
                    if (needsManagedInstall)
                    {
                        Log.Info($"switching app-managed llama backend to {InferenceRuntime.Normalize(RuntimeBackend)}");
                        if (!await InstallAsync(cancel)) return false;
                        return await StartServerAsync(cancel);
                    }
                    BinaryPath = resolved.Path;
                    CurrentOrigin = Origin.Managed;
                    Version = await ReadVersionAsync(resolved.Path!, cancel);
                    State = InstallState.Idle;
                    return await StartServerAsync(cancel);

                case ResolutionKind.External:
                    BinaryPath = resolved.Path;
                    CurrentOrigin = Origin.External;
                    Version = await ReadVersionAsync(resolved.Path!, cancel);
                    State = InstallState.Idle;
                    return await StartServerAsync(cancel);

                default: // Missing — download then launch.
                    if (await InstallAsync(cancel))
                        return await StartServerAsync(cancel);
                    return false;
            }
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>
    /// Probes <c>GET /health</c> on the server port once. Any HTTP response
    /// means a server is already listening (connection-refused means not). The
    /// atomic unit used by the retrying <see cref="WaitForReachableAsync"/> and
    /// by the last-chance re-probe in <see cref="StartServerAsync"/>.
    /// </summary>
    /// <summary>
    /// A linked token that cancels after <paramref name="timeout"/> — the
    /// per-call time budget replacing <see cref="HttpClient.Timeout"/> (the
    /// shared <see cref="_http"/> runs with an infinite timeout so SSE streams
    /// aren't cut).
    /// </summary>
    private static CancellationTokenSource WithTimeout(TimeSpan timeout, CancellationToken cancel)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        return cts;
    }

    private async Task<bool> ProbeHealthAsync(CancellationToken cancel)
    {
        try
        {
            // 5s per-call budget: a refused connection (no server) fails
            // instantly regardless — the budget only bounds a server that is
            // listening but slow to answer (busy loading a model). The old
            // per-call client with a 1s timeout and system-proxy defaults is
            // what made this return false while a browser got 200 OK.
            using var budget = WithTimeout(TimeSpan.FromSeconds(5), cancel);
            using var resp = await _http.GetAsync("/health", budget.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Repeatedly probes <c>/health</c> for up to <paramref name="timeout"/>,
    /// returning <c>true</c> as soon as a server responds. Used to ADOPT an
    /// already-running server (a sibling app instance, a manual launch, or one
    /// that's mid-bind) rather than spawning a duplicate on the same port — the
    /// fix for several <c>llama serve</c> processes piling up and eating RAM. The window is short, so a genuinely absent server doesn't
    /// delay startup by much (each refusal is near-instant; the 250ms cadence
    /// is what bounds the worst case).
    /// </summary>
    private async Task<bool> WaitForReachableAsync(TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            if (await ProbeHealthAsync(cancel)) return true;
            
            await Task.Delay(100, cancel);
        }
        return false;
    }

    /// <summary>
    /// Best-effort binary resolution and version read for an adopted (external)
    /// server — populates <see cref="BinaryPath"/>/<see cref="Version"/> for
    /// display without blocking the client. Fire-and-forget.
    /// </summary>
    private async Task ResolveAndReadVersionAsync(CancellationToken cancel)
    {
        try
        {
            var resolved = Resolve();
            if (resolved.Path is { } p && File.Exists(p))
            {
                BinaryPath = p;
                CurrentOrigin = resolved.Kind == ResolutionKind.Managed ? Origin.Managed : Origin.External;
                Version = await ReadVersionAsync(p, cancel);
            }
        }
        catch (Exception ex) { Log.Warn(ex, "best-effort version resolve failed"); }
    }

    /// <summary>
    /// (Re)installs the app-managed binary by downloading and executing
    /// <see cref="InstallScriptUrl"/>. Also, the retry entry point.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken cancel = default, bool allowUnpinnedInstall = true)
    {
        State = InstallState.Installing;
        try
        {
            await DownloadAndRunInstallerAsync(RuntimeBackend, allowUnpinnedInstall, cancel);
            // Exit code 0 = success (DownloadAndRunInstallerAsync throws
            // otherwise): llama is now on PATH. Resolve its absolute path
            // dynamically ("which") instead of assuming a fixed location —
            // FindOnPath also reads the registry user/machine PATH, which is
            // what sees a PATH entry the installer just added (a child process
            // can't update our own environment block).
            BinaryPath = FindOnPath("llama.exe")
                ?? throw new IOException("Install script succeeded but 'llama' was not found on PATH.");
            CurrentOrigin = IsManagedPath(BinaryPath) ? Origin.Managed : Origin.External;
            Version = await ReadVersionAsync(BinaryPath, cancel);
            // Record the installer choice even if it used its CPU fallback.
            // Hardware availability is handled at launch, not by reinstalling.
            WriteAppliedRuntime(RuntimeBackend);
            _devicesCache = default; // A replacement binary may expose a different backend.
            _serveCapabilities = new(false, false, false, false);
            State = InstallState.Idle;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "llama binary install failed");
            FailureMessage = ex.Message;
            State = InstallState.Failed;
            return false;
        }
    }

    // ---- Server ----

    /// <summary>
    /// Builds the argv for a server the app launches. Kept as a pure function
    /// so the launch flag matrix (which flags are always passed, which are
    /// conditional on their value, and in what order) is unit-testable without
    /// spawning a process. The fixed prefix is <c>serve --host &lt;addr&gt;
    /// --port &lt;port&gt; --jinja</c>; <c>0</c>/negative
    /// <paramref name="maxLoadedModels"/> means unlimited and omits
    /// <c>--models-max</c>; supported non-empty
    /// <paramref name="cacheTypeK"/>/<paramref name="cacheTypeV"/> values add
    /// <c>--cache-type-k</c>/<c>--cache-type-v</c>; a non-null
    /// <paramref name="contextPresetPath"/> adds <c>--models-preset</c>; a
    /// non-empty <paramref name="apiKeyFilePath"/> adds
    /// <c>--api-key-file</c> (only set for a non-loopback bind — the key
    /// itself is NEVER a process argument, which any local user can read via
    /// WMI); <paramref name="customArguments"/> tokens are
    /// appended LAST so they can override the built-in flags (llama.cpp honors
    /// the last occurrence) — except the reserved bind/auth flags, which
    /// <see cref="ServeArgumentPolicy"/> refuses before this is reached.
    /// </summary>
    internal static List<string> BuildServeArguments(
        int serverPort,
        string listenAddress,
        int idleUnloadSeconds,
        int maxLoadedModels,
        string? cacheTypeK,
        string? cacheTypeV,
        string? contextPresetPath,
        IReadOnlyList<string>? customArguments,
        string? deviceId = null,
        string? gpuLayers = null,
        int batchSize = 0,
        int microBatchSize = 0,
        string? flashAttention = "auto",
        string? apiKeyFilePath = null)
    {
        // `serve` is the unified subcommand (replaces the old llama-server).
        // Router mode hosts the webui and serves requests even with no model
        // loaded — models load on demand. --host pins the server to the
        // interface the user picked; --jinja enables chat templates.
        var args = new List<string>
        {
            "serve",
            "--host",
            listenAddress,
            "--port",
            serverPort.ToString(),
            "--jinja",
        };

        // Non-loopback binds get an app-generated key so the server isn't an
        // open control API on the network. Handed over as a per-user key FILE
        // (llama.cpp reads the first line) rather than a --api-key argument —
        // the process command line is readable by any local user via WMI.
        // App-owned, so it is never overridable from custom arguments
        // (ServeArgumentPolicy reserves --api-key and --api-key-file).
        if (!string.IsNullOrWhiteSpace(apiKeyFilePath))
        {
            args.Add("--api-key-file");
            args.Add(apiKeyFilePath);
        }

        // Idle model unload. Done server-side because the server is the only
        // place that sees ALL model traffic — the overlay's WebUI chats
        // straight with it, invisible to the app, so an app-side idle timer
        // could unload a model mid-conversation. The router propagates the
        // flag to each per-model child server.
        if (idleUnloadSeconds > 0)
        {
            args.Add("--sleep-idle-seconds");
            args.Add(idleUnloadSeconds.ToString());
        }

        // Max loaded models. Router mode loads each model in its own child
        // process; 0 (the default) leaves the server's unlimited behavior,
        // otherwise the router refuses to exceed N simultaneous loads.
        if (maxLoadedModels > 0)
        {
            args.Add("--models-max");
            args.Add(maxLoadedModels.ToString());
        }

        // KV cache quantization. K and V are configured independently so a
        // user can, for example, keep keys in q8_0 while values stay f16 when
        // quality is more sensitive to one side. Only values the server
        // understands are forwarded; unrecognized hand-edited settings are
        // ignored rather than passed as an invalid flag.
        if (!string.IsNullOrWhiteSpace(cacheTypeK) &&
            SupportedKvCacheTypes.Contains(cacheTypeK, StringComparer.Ordinal))
        {
            args.Add("--cache-type-k");
            args.Add(cacheTypeK);
        }
        if (!string.IsNullOrWhiteSpace(cacheTypeV) &&
            SupportedKvCacheTypes.Contains(cacheTypeV, StringComparer.Ordinal))
        {
            args.Add("--cache-type-v");
            args.Add(cacheTypeV);
        }

        // Per-model context lengths. The router's /models/load ignores a ctx
        // field in the request body (it reads only the model name), so the
        // chosen sizes are rendered into a --models-preset INI that the router
        // merges into each model's child args (--ctx-size N).
        if (contextPresetPath is not null)
        {
            args.Add("--models-preset");
            args.Add(contextPresetPath);
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            args.Add("--device");
            args.Add(deviceId);
        }
        var layers = deviceId == "none" ? "0" : InferenceRuntime.GpuLayersArgument(gpuLayers);
        if (layers is not null)
        {
            args.Add("--n-gpu-layers");
            args.Add(layers);
        }

        // Leave all three defaults to llama.cpp. Hand-edited invalid settings
        // are rejected before a managed launch; this guard also keeps the pure
        // argument builder safe for any other caller.
        if (InferenceTuning.Validate(batchSize, microBatchSize, flashAttention) is null)
        {
            if (batchSize > 0)
            {
                args.Add("--batch-size");
                args.Add(batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (microBatchSize > 0)
            {
                args.Add("--ubatch-size");
                args.Add(microBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (flashAttention is "on" or "off")
            {
                args.Add("--flash-attn");
                args.Add(flashAttention);
            }
        }

        // Custom arguments go last so they win when llama.cpp keeps the
        // final occurrence of a repeated flag — the user's typed text is the
        // most specific intent.
        if (customArguments is { Count: > 0 })
            args.AddRange(customArguments);

        return args;
    }

    /// <summary>
    /// Formats the user-facing reason for a failed launch from the captured
    /// stderr tail. llama.cpp writes its INFO chatter to stderr too, so the
    /// last line that mentions an error wins; without one, the last non-blank
    /// line is the best available clue. Kept pure so the phrasing is
    /// unit-testable; the footer presentation sanitizes it further.
    /// </summary>
    internal static string FormatStartFailureMessage(string? stderrTail)
    {
        if (string.IsNullOrWhiteSpace(stderrTail))
            return "The llama server failed to start.";

        var lines = stderrTail
            .Split('\n')
            .Select(line => line.Trim().TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0)
            return "The llama server failed to start.";

        var errorLine = lines.LastOrDefault(line =>
            line.Contains("error", StringComparison.OrdinalIgnoreCase));
        var chosen = errorLine ?? lines[^1];
        return $"The llama server failed to start: {chosen}";
    }

    /// <summary>
    /// Launches <c>llama serve --port 2276</c> as a background process and polls
    /// the port until it responds (or times out). Called automatically by
    /// <see cref="EnsureLlamaOrDownloadAsync"/> once a binary is available. No-op (returns
    /// true) if the server is already running.
    /// </summary>
    public async Task<bool> StartServerAsync(CancellationToken cancel = default)
    {
        HipSdkEnvironment.EnsureOnProcessPath();
        if (ServerStatus == ServerState.Running) return true;
        if (BinaryPath is null || !File.Exists(BinaryPath))
        {
            FailureMessage = "The llama server binary is missing.";
            ServerStatus = ServerState.Failed;
            return false;
        }

        // Last-chance adoption: between EnsureLlamaOrDownloadAsync's probe and
        // now (esp. after a slow install.ps1 download), a sibling instance or a
        // manual launch may have brought up a server on our port. Adopting it
        // here avoids spawning a duplicate that would fail to bind and orphan
        // — the exact leak that left several servers eating RAM.
        if (await ProbeHealthAsync(cancel))
        {
            Log.Info("adopted an already-running llama server (pre-start re-probe)");
            FailureMessage = null;
            ServerStatus = ServerState.Running;
            _ = ResolveAndReadVersionAsync(cancel);
            return true;
        }

        // A live MANAGED server (valid PID file) that isn't responding yet is
        // still ours — the app may have crashed and restarted while the server
        // was mid-startup. DON'T kill it: give it a grace window to come up and
        // adopt it. Only if it never responds (genuinely hung) do we reclaim
        // it below — it's ours, so killing is safe.
        if (ReadLiveManagedPid(PidFilePath) is { } managedPid)
        {
            Log.Info($"managed llama server (pid {managedPid}) is alive but not reachable yet; waiting for it");
            ServerStatus = ServerState.Starting;
            if (await WaitForReachableAsync(TimeSpan.FromSeconds(15), cancel))
            {
                Log.Info($"adopted the managed llama server (pid {managedPid})");
                FailureMessage = null;
                ServerStatus = ServerState.Running;
                _ = ResolveAndReadVersionAsync(cancel);
                return true;
            }
            Log.Warn($"managed llama server (pid {managedPid}) never became reachable; killing and relaunching");
        }

        StopServer(); // reclaim any prior instance / port

        // Tokenize the custom arguments BEFORE flipping to Starting: an open
        // quote is a settings error, not a launch attempt, and must fail fast
        // with the parser's message rather than a 15s port timeout.
        IReadOnlyList<string> customArguments;
        try
        {
            customArguments = Common.ArgumentTokenizer.Tokenize(CustomServeArguments);
        }
        catch (FormatException ex)
        {
            Log.Error(ex, "custom serve arguments failed to parse");
            FailureMessage = ex.Message;
            ServerStatus = ServerState.Failed;
            return false;
        }

        // Defense in depth: the Settings window also validates reserved flags,
        // but a hand-edited settings.json must not be able to silently override
        // --host/--port (exposing the server) or --api-key (disabling auth).
        if (Common.ServeArgumentPolicy.Validate(customArguments) is { } reservedError)
        {
            Log.Error($"custom serve arguments rejected: {reservedError}");
            FailureMessage = reservedError;
            ServerStatus = ServerState.Failed;
            return false;
        }

        if (InferenceTuning.Validate(BatchSize, MicroBatchSize, FlashAttention) is { } tuningError)
        {
            FailureMessage = tuningError;
            ServerStatus = ServerState.Failed;
            return false;
        }

        var globalProfile = BuildGlobalInferenceProfile();
        if (InferenceTuning.ValidateProfile(globalProfile) is { } globalError)
        {
            FailureMessage = globalError;
            ServerStatus = ServerState.Failed;
            return false;
        }

        _serveCapabilities = new(false, false, false, false);
        if (!globalProfile.IsAutomatic ||
            ModelPromptProfiles?.Values.Any(profile => profile is not null && !profile.IsAutomatic) == true)
        {
            _serveCapabilities = await ServeCapabilities.ProbeAsync(BinaryPath, cancel);
            if (!globalProfile.IsAutomatic && InferenceTuning.UnsupportedProfileOption(globalProfile,
                    _serveCapabilities) is { } unsupported)
            {
                FailureMessage = unsupported.StartsWith("--", StringComparison.Ordinal)
                    ? $"The installed llama server does not support {unsupported}."
                    : unsupported;
                ServerStatus = ServerState.Failed;
                return false;
            }
        }
        UpdateModelProfileWarning();

        // A fresh attempt invalidates whatever the previous launch failed
        // with; a new reason is set below if this one fails too.
        FailureMessage = null;
        ServerStatus = ServerState.Starting;

        try
        {
            await _additionalModels.RefreshAsync(AdditionalModelDirectories, cancel, force: true);
            var needsDeviceProbe = InferenceRuntime.Normalize(RuntimeBackend) != InferenceRuntime.Automatic
                || !string.IsNullOrWhiteSpace(GpuDeviceName)
                || ModelPromptProfiles?.Values.Any(profile => profile is not null &&
                    !string.IsNullOrEmpty(profile.GpuDeviceName) && profile.GpuDeviceName != "none") == true;
            var probe = needsDeviceProbe
                ? await ProbeDevicesAsync(cancel)
                : new DeviceProbe(false, []);
            var selection = InferenceRuntime.SelectDevice(RuntimeBackend, GpuDeviceName, probe);
            _lastDeviceProbe = probe;
            ActiveDeviceId = selection.DeviceArgument;
            RuntimeFallbackReason = selection.FallbackReason;
            if (RuntimeFallbackReason is not null) Log.Warn(RuntimeFallbackReason);

            var psi = new ProcessStartInfo
            {
                FileName = BinaryPath,
                // Pin the working directory to the binary's own folder so the
                // server never inherits (or resolves relative custom paths
                // against) an unpredictable app CWD.
                WorkingDirectory = Path.GetDirectoryName(BinaryPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Per-model context lengths are merged into each model's child args
            // via --models-preset (see BuildServeArguments). Written fresh every
            // launch from the current preferences.
            var contextPresetPath = WriteModelPresetsIni(selection.DeviceArgument, probe);
            // Materialize the app-generated key (non-loopback binds) into a
            // per-user file right before launch: the server reads it via
            // --api-key-file, so the key never appears in argv, process
            // listings, or WMI queries. Rewritten every launch; the value is
            // stable (persisted in settings.json) so an adopted server keeps
            // accepting it across app restarts.
            string? apiKeyFilePath = null;
            if (ApiKey is not null)
            {
                apiKeyFilePath = Path.Combine(Common.AppData.Root, ".llama.apikey");
                Directory.CreateDirectory(Path.GetDirectoryName(apiKeyFilePath)!);
                File.WriteAllText(apiKeyFilePath, ApiKey);
            }
            foreach (var arg in BuildServeArguments(
                         ServerPort, ListenAddress, IdleUnloadSeconds, MaxLoadedModels,
                         null, null, contextPresetPath, customArguments,
                         apiKeyFilePath: apiKeyFilePath))
                psi.ArgumentList.Add(arg);

            // Point the HF cache at the user-configured directory so the server
            // resolves downloaded models from the same place the app scans.
            if (!string.IsNullOrEmpty(CacheDirectory) && Directory.Exists(CacheDirectory))
                psi.EnvironmentVariables["HF_HUB_CACHE"] = CacheDirectory;

            // Hand the server the user's HF access token (if any) so it can
            // download private/gated models. Passed as an environment variable
            // — llama.cpp has no token CLI flag, and an arg would be visible in
            // process listings. Log presence only, never the value.
            var hfToken = HuggingFaceToken?.Trim();
            if (!string.IsNullOrEmpty(hfToken))
            {
                psi.EnvironmentVariables["HF_TOKEN"] = hfToken;
                Log.Info("HF token configured; passing HF_TOKEN to the llama server");
            }

            var customArgsText = customArguments.Count == 0
                ? ""
                : " " + Common.ServeArgumentPolicy.RedactForLog(customArguments);
            Log.Info($"starting llama server: {BinaryPath} serve --host {ListenAddress} --port {ServerPort} --jinja" +
                (ApiKey is not null ? $" --api-key-file {apiKeyFilePath}" : "") +
                (IdleUnloadSeconds > 0 ? $" --sleep-idle-seconds {IdleUnloadSeconds}" : "") +
                (MaxLoadedModels > 0 ? $" --models-max {MaxLoadedModels}" : "") +
                (contextPresetPath is not null ? " --models-preset <local preset>" : "") +
                customArgsText);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) =>
            {
                // Log-only: server STATUS is derived from API polls by the
                // supervisor (see SupervisorLoopAsync), never from process
                // handles — an adopted server has no handle to watch, and a
                // spawned one's death is detected just as fast via refused
                // connections on the next poll tick.
                Log.Info($"llama server process exited (code={proc.ExitCode})");
            };

            if (!proc.Start())
            {
                Log.Error("llama server process failed to start (proc.Start returned false)");
                FailureMessage = "The llama server process failed to start.";
                ServerStatus = ServerState.Failed;
                return false;
            }
            _serverProcess = proc;
            // Track ownership across app restarts: if the app crashes, the next
            // instance recognizes this server as managed via the PID file.
            WritePidFile(proc.Id);

            // The server's stdout/stderr are redirected; drain them or a chatty
            // server blocks once the pipe buffer fills. Draining also captures
            // server errors (e.g. a bad launch flag) into the app log — and, via
            // the stderr tail, into the user-facing failure reason below.
            var stderrTail = new ServerOutputTail();
            var (stdoutTask, stderrTask) = DrainServerOutput(proc, stderrTail);

            // Wait for the port to respond — the server takes a moment to bind.
            // We pass `proc` so the wait fast-fails if the process exits before
            // becoming ready (e.g. it couldn't bind the port because a sibling
            // already did) instead of polling for the full 15s timeout.
            if (await WaitForPortAsync(proc, TimeSpan.FromSeconds(15), cancel))
            {
                Log.Info("llama server is reachable");
                FailureMessage = null;
                ServerStatus = ServerState.Running;
                return true;
            }

            // Timed out (or the process exited early). DON'T leave the spawned
            // process running: a prior timeout-then-abandon left the server
            // alive, and a later app start (or this same retry) spawned a
            // second on the same port → two servers eating RAM. Kill ours so
            // the port is free for the next attempt. StopServer sets Stopped
            // before killing (deliberate intent — the supervisor never leaves
            // Stopped on its own), then we surface the failure.
            Log.Error("llama server failed to become ready within 15s (port probe timed out)");
            // The pipes close when the process exits, so the drain tasks finish
            // almost immediately in the died-at-startup case; the timeout only
            // matters for a genuinely hung server, where we surface whatever
            // stderr arrived before giving up.
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(500)); }
            catch { /* snapshot whatever arrived */ }
            FailureMessage = FormatStartFailureMessage(stderrTail.Snapshot());
            StopServer();
            ServerStatus = ServerState.Failed;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "llama server start threw");
            FailureMessage = ex.Message;
            ServerStatus = ServerState.Failed;
            return false;
        }
    }

    /// <summary>
    /// Called after the managed install folder was emptied (from Settings):
    /// drops the stale binary/version/origin so the UI stops advertising an
    /// installation that no longer exists. The next
    /// <see cref="EnsureLlamaOrDownloadAsync"/> re-resolves from scratch and
    /// reinstalls on demand. No-op for external installations — they are not
    /// the app's to remove.
    /// </summary>
    public void NotifyManagedInstallRemoved()
    {
        if (CurrentOrigin != Origin.Managed) return;
        BinaryPath = null;
        Version = null;
        CurrentOrigin = Origin.Unknown;
    }

    /// <summary>
    /// Stops the running <b>managed</b> server (if any). Safe to call repeatedly.
    /// Sets state to <see cref="ServerState.Stopped"/> before killing: Stopped
    /// marks the stop as deliberate app intent, and the supervisor never
    /// probes or transitions out of Stopped on its own (see
    /// <see cref="DeriveServerStatus"/>).
    ///
    /// <para>"Managed" = an app instance started the server: either this one
    /// (we hold the process handle) or a previous one that crashed — proven by
    /// the <c>.llama.pid</c> file (<see cref="ReadLiveManagedPid"/>). A server
    /// with no valid PID file (started manually by the user, whatever the
    /// binary) is not ours and is left running.</para>
    /// </summary>
    public void StopServer()
    {
        ServerStatus = ServerState.Stopped;

        var proc = _serverProcess;
        _serverProcess = null;

        if (proc is not null)
        {
            // Spawned this session. Clear the PID file only if it still tracks
            // THIS process — a racing instance may have rewritten it for a
            // newer server.
            if (ReadPidFile(PidFilePath) == proc.Id) DeletePidFile();
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { Log.Warn(ex, "best-effort server kill failed"); }
            }
            return;
        }

        // Adopted managed server (started by a previous/crashed instance): no
        // handle of ours, but a valid PID file proves ownership — kill by PID.
        if (ReadLiveManagedPid(PidFilePath) is { } managedPid)
        {
            DeletePidFile();
            try
            {
                Log.Info($"killing managed llama server by PID file (pid {managedPid})");
                Process.GetProcessById(managedPid).Kill(entireProcessTree: true);
            }
            catch (Exception ex) { Log.Warn(ex, $"best-effort managed server kill failed (pid {managedPid})"); }
        }
    }

    // ---- Managed-server PID file ----

    /// <summary>
    /// Path of the PID file tracking the managed llama server:
    /// <c>%LOCALAPPDATA%\Llama\.llama.pid</c>. Written by
    /// <see cref="StartServerAsync"/> right after the server process is spawned;
    /// read back after an app crash/restart to recognize the surviving server
    /// as ours (managed) — and therefore safe to stop. Deleted when the managed
    /// server is stopped or found dead.
    /// </summary>
    private static string PidFilePath =>
        Path.Combine(AppData.Root, ".llama.pid");

    /// <summary>
    /// Path of the llama.cpp <c>--models-preset</c> INI the app generates from
    /// <see cref="ModelContextLengths"/> and <see cref="ModelPromptProfiles"/> —
    /// one <c>[repo:quant]</c> section with the model's overrides. Rewritten
    /// on every server start and preference change.
    ///
    /// <para>Deliberately NOT under <see cref="AppData.Root"/>: the app is
    /// MSIX-packaged, so writes to <c>%LOCALAPPDATA%</c> are virtualized (the
    /// file physically lands under <c>Packages\…\LocalCache</c>) while the path
    /// string stays the real <c>%LOCALAPPDATA%</c> one — and the non-packaged
    /// server then reads a path where no file exists (launch fails with
    /// "preset file does not exist"). The user profile is not virtualized, so
    /// app and server see the same file there.</para>
    /// </summary>
    private static string ModelPresetPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".llama", "model-presets.ini");

    /// <summary>
    /// Renders model-specific context and prompt-processing preferences as a
    /// llama.cpp preset INI, or returns <c>null</c> when there are none.
    /// </summary>
    private string? RenderModelPresets(string? globalDeviceId = null, DeviceProbe? probe = null)
    {
        var devices = (probe ?? _lastDeviceProbe).Devices
            .Where(device => device.Kind is not (DeviceKind.Cpu or DeviceKind.Unknown))
            .ToArray();
        var deviceIds = devices.ToDictionary(
            device => GpuDeviceChoice.Key(device, devices), device => device.Id,
            StringComparer.OrdinalIgnoreCase);
        var profiles = InferenceTuning.CompatibleModelProfiles(ModelPromptProfiles,
            BatchSize, MicroBatchSize, _serveCapabilities, out _);
        return ModelPresets.Render(ModelContextLengths, profiles,
            BuildGlobalInferenceProfile(), deviceIds, globalDeviceId, _additionalModels.Snapshot);
    }

    private void UpdateModelProfileWarning()
    {
        InferenceTuning.CompatibleModelProfiles(ModelPromptProfiles,
            BatchSize, MicroBatchSize, _serveCapabilities, out var warnings);
        var warning = warnings.Count == 0 ? null :
            "Some saved model overrides were not applied. Open the model's details to edit or Reset them.\n" +
            string.Join('\n', warnings);
        if (warning is not null && warning != ModelProfileWarning) Log.Warn(warning);
        ModelProfileWarning = warning;
    }

    /// <summary>Preview saved router arguments and per-model preset values.</summary>
    public string PreviewInferenceConfiguration()
    {
        IReadOnlyList<string> custom;
        try { custom = Common.ArgumentTokenizer.Tokenize(CustomServeArguments); }
        catch (FormatException ex) { return "Custom arguments are invalid: " + ex.Message; }
        var preset = RenderModelPresets(ActiveDeviceId);
        var args = BuildServeArguments(ServerPort, ListenAddress, IdleUnloadSeconds,
            MaxLoadedModels, null, null, preset is null ? null : "<model-presets.ini>", custom);
        static string Quote(string value) => value.Any(char.IsWhiteSpace)
            ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
        return "Router configuration loaded at app start:\nllama.exe " +
            string.Join(' ', args.Select(Quote)) +
            "\n\nModel presets (applied when each model loads):\n" +
            (preset ?? "(none; llama.cpp defaults)") +
            (ModelProfileWarning is null ? "" : "\n\n" + ModelProfileWarning);
    }

    /// <summary>
    /// Keeps a preset file available even before preferences exist, so a running
    /// router can reload preferences added later. A write failure fails launch.
    /// </summary>
    private string? WriteModelPresetsIni(string? globalDeviceId = null, DeviceProbe? probe = null)
    {
        var ini = RenderModelPresets(globalDeviceId, probe) ?? string.Empty;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPresetPath)!);
            File.WriteAllText(ModelPresetPath, ini);
            return ModelPresetPath;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "failed to write model context preset file");
            throw new IOException("Could not write the model inference preset.", ex);
        }
    }

    /// <summary>
    /// Re-renders the model-preset INI from context and prompt preferences
    /// and asks the running server to re-read its presets
    /// (<c>GET /models?reload=1</c> re-runs the router's preset load, which
    /// re-reads the <c>--models-preset</c> file). Called when the user changes a
    /// model's context or prompt profile so the next <c>/models/load</c> picks it up.
    /// Reloading does not disturb already-loaded models. Best-effort: a server
    /// the app didn't launch (no <c>--models-preset</c> flag) won't read the
    /// file — the preference then takes effect on the next app-launched start.
    /// </summary>
    public async Task ReloadModelPresetsAsync(CancellationToken cancel = default)
    {
        try
        {
            await _additionalModels.RefreshAsync(AdditionalModelDirectories, cancel);
            if (!_serveCapabilities.Succeeded && BinaryPath is not null &&
                ModelPromptProfiles?.Values.Any(profile => profile is not null && !profile.IsAutomatic) == true)
                _serveCapabilities = await ServeCapabilities.ProbeAsync(BinaryPath, cancel);
            UpdateModelProfileWarning();
            if (ModelPromptProfiles?.Values.Any(profile => profile is not null &&
                !string.IsNullOrEmpty(profile.GpuDeviceName) && profile.GpuDeviceName != "none") == true)
                _lastDeviceProbe = await ProbeDevicesAsync(cancel);
            WriteModelPresetsIni(ActiveDeviceId, _lastDeviceProbe);
            if (ServerStatus != ServerState.Running) return;
            using var budget = WithTimeout(TimeSpan.FromSeconds(10), cancel);
            using var resp = await _http.GetAsync("/models?reload=1", budget.Token);
            Log.Info($"model context presets reloaded (HTTP {(int)resp.StatusCode})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug("model context preset reload failed: " + ex.Message);
        }
    }

    /// <summary>Writes <paramref name="pid"/> to the PID file. Best-effort.</summary>
    private static void WritePidFile(int pid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
            File.WriteAllText(PidFilePath, pid.ToString());
        }
        catch (Exception ex) { Log.Warn(ex, "best-effort PID file write failed"); }
    }

    /// <summary>
    /// A bounded, thread-safe tail of the server's stderr. The drain task
    /// appends every line; a failed launch reads the tail back so the footer
    /// can say WHY the server died (llama.cpp prints its errors to stderr).
    /// </summary>
    private sealed class ServerOutputTail
    {
        private const int Capacity = 20;
        private readonly object _gate = new();
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Capacity)
                    _lines.Dequeue();
            }
        }

        /// <summary>The captured lines joined with newlines, or null when none.</summary>
        public string? Snapshot()
        {
            lock (_gate)
            {
                return _lines.Count == 0 ? null : string.Join(Environment.NewLine, _lines);
            }
        }
    }

    /// <summary>
    /// Drains a spawned server's redirected stdout/stderr on background threads.
    /// Redirecting without draining lets a chatty server block once the pipe
    /// buffer fills (a silent stall); draining also keeps server output available
    /// for diagnosing a failed launch. llama.cpp writes everything (INFO
    /// included) to stderr, so both streams log at Debug — anything higher would
    /// spam the app log with per-request noise. The returned tasks let a failed
    /// launch wait for the pipes to close before snapshotting the stderr tail.
    /// </summary>
    private static (Task Stdout, Task Stderr) DrainServerOutput(
        Process proc, ServerOutputTail? stderrTail = null)
    {
        Task Drain(StreamReader reader, string tag, ServerOutputTail? tail)
        {
            return Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        Log.Debug($"[llama{tag}] {line}");
                        tail?.Add(line);
                    }
                }
                catch { /* stream closed when the server exits */ }
            });
        }

        return (Drain(proc.StandardOutput, "", null), Drain(proc.StandardError, " stderr", stderrTail));
    }

    private static void DeletePidFile() => DeletePidFile(PidFilePath);

    /// <summary>Deletes the PID file if present. Best-effort.</summary>
    private static void DeletePidFile(string pidFilePath)
    {
        try { if (File.Exists(pidFilePath)) File.Delete(pidFilePath); }
        catch (Exception ex) { Log.Warn(ex, "best-effort PID file delete failed"); }
    }

    /// <summary>
    /// Raw PID-file parse: the stored PID, or <c>null</c> when the file is
    /// missing or unreadable. Garbage content is deleted rather than kept.
    /// </summary>
    private static int? ReadPidFile(string pidFilePath)
    {
        string text;
        try
        {
            if (!File.Exists(pidFilePath)) return null;
            text = File.ReadAllText(pidFilePath).Trim();
        }
        catch (Exception ex) { Log.Warn(ex, "PID file read failed"); return null; }

        if (int.TryParse(text, out var pid) && pid > 0) return pid;

        DeletePidFile(pidFilePath); // garbage — don't keep it around
        return null;
    }

    /// <summary>
    /// Crash-safe managed-server check: the PID from
    /// <paramref name="pidFilePath"/>, but only if that process is still alive
    /// AND is actually a llama server — guarding against PID reuse (the OS
    /// recycling our dead server's PID for an unrelated process): the process
    /// must be named <c>llama</c> and must have started before the PID file was
    /// written (we write right after <see cref="Process.Start"/>). A stale or
    /// mismatched file is deleted so the check stays cheap. Internal for tests.
    /// </summary>
    internal static int? ReadLiveManagedPid(string pidFilePath)
    {
        if (ReadPidFile(pidFilePath) is not { } pid) return null;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var isLlama = string.Equals(proc.ProcessName, "llama", StringComparison.OrdinalIgnoreCase);
            var startedBeforeWrite =
                proc.StartTime.ToUniversalTime() <= File.GetLastWriteTimeUtc(pidFilePath) + TimeSpan.FromSeconds(5);
            if (isLlama && startedBeforeWrite) return pid;
        }
        catch (ArgumentException) { /* no such process — stale file */ }
        catch (Exception ex)
        {
            // Couldn't verify (e.g., access denied): don't kill what we can't
            // identify, but keep the file for a later re-check.
            Log.Warn(ex, $"managed-server PID check failed (pid {pid})");
            return null;
        }

        DeletePidFile(pidFilePath); // stale or PID reused — clean up
        return null;
    }

    /// <summary>
    /// Polls <c>GET /health</c> on the configured port until it responds or the timeout
    /// elapses (or the spawned <paramref name="proc"/> exits first). The llama
    /// server exposes a health endpoint once it's bound and ready; this confirms
    /// the port is actually serving rather than just waiting for a fixed delay.
    /// Checking <c>proc.HasExited</c> each iteration fast-fails when the process
    /// died right after launch (e.g., it couldn't bind the port because a
    /// sibling already did) so we don't sit out the full timeout before tearing
    /// down — and we don't keep around a dead-but-tracked process reference.
    /// </summary>
    private async Task<bool> WaitForPortAsync(Process proc, TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                Log.Warn($"llama server process exited before becoming ready (code={proc.ExitCode})");
                return false;
            }
            try
            {
                // Any HTTP response (even an error code) means the server is
                // up and listening — a connection-refused means it's not yet.
                using var budget = WithTimeout(TimeSpan.FromSeconds(2), cancel);
                using var resp = await _http.GetAsync("/health", budget.Token);
                return true;
            }
            catch
            {
                await Task.Delay(250, cancel);
            }
        }
        return false;
    }

    // ---- Server-status supervisor ----

    /// <summary>
    /// Pure derivation of <see cref="ServerState"/> from a single API probe —
    /// the ONLY source of truth for server status. No process-handle
    /// assumptions: a spawned server's crash and an adopted server's crash
    /// look identical to the poll, and a server that reappears is adopted the
    /// same way no matter who (re)started it.
    /// </summary>
    /// <param name="current">The state observed before the probe.</param>
    /// <param name="apiReachable">Whether the HTTP API answered (any HTTP
    /// response counts; connection-refused/timeout counts as not).</param>
    public static ServerState DeriveServerStatus(ServerState current, bool apiReachable) =>
        (current, apiReachable) switch
        {
            (ServerState.Running, true) => ServerState.Running,
            // Was running, now unreachable → crashed (or the machine/network did).
            (ServerState.Running, false) => ServerState.Failed,
            // A launch is confirmed by the API answering, not by the process
            // having started.
            (ServerState.Starting, true) => ServerState.Running,
            // Still booting — StartServerAsync's own wait bounds the window.
            (ServerState.Starting, false) => ServerState.Starting,
            // Auto-recovery: a server (re)appeared — e.g. the user restarted
            // their own instance after a crash. Adopt it; no relaunch click
            // needed.
            (ServerState.Failed, true) => ServerState.Running,
            (ServerState.Failed, false) => ServerState.Failed,
            // Stopped is deliberate app intent (startup before the ensure
            // pipeline runs, the port-reclaim window inside StartServerAsync,
            // app exit). The supervisor never probes while Stopped and never
            // leaves it on its own — otherwise it could "adopt" a process
            // StopServer is in the middle of killing. Leaving Stopped is
            // always explicit: EnsureLlamaOrDownloadAsync / the relaunch button.
            (ServerState.Stopped, _) => ServerState.Stopped,
        };

    /// <summary>
    /// Applies a <see cref="DeriveServerStatus"/> result, with logging on the
    /// two transitions that matter operationally: declaring a crash
    /// (<see cref="ServerState.Failed"/>) and confirming/adopting a server
    /// (<see cref="ServerState.Running"/>).
    /// </summary>
    private void ApplyPolledStatus(ServerState derived)
    {
        if (derived == ServerStatus) return;
        if (derived == ServerState.Failed)
        {
            Log.Warn("llama server unreachable; declaring it failed (API-polled)");
            FailureMessage = "The llama server stopped responding.";
        }
        else if (derived == ServerState.Running)
        {
            Log.Info($"llama server reachable; {ServerStatus} → Running (API-polled)");
        }
        ServerStatus = derived;
    }

    /// <summary>
    /// The always-on supervisor loop — started once in the constructor and run
    /// for the app's whole lifetime. It is the single place that turns API
    /// answers into <see cref="ServerStatus"/> transitions:
    /// <list type="bullet">
    /// <item>While <see cref="ServerState.Running"/>: fetch <c>GET /models</c>
    /// every 500ms — the fetch doubles as the liveness probe AND publishes the
    /// snapshot via <see cref="ModelsChanged"/>. A failed fetch is confirmed
    /// with a <c>/health</c> probe before declaring death, so a transient
    /// <c>/models</c> hiccup on a living server doesn't flip the state.</item>
    /// <item>While <see cref="ServerState.Starting"/> or
    /// <see cref="ServerState.Failed"/>: probe <c>/health</c> every second — a
    /// reachable API confirms a launch or adopts a (re)appeared server.</item>
    /// <item>While <see cref="ServerState.Stopped"/>: idle — see
    /// <see cref="DeriveServerStatus"/>.</item>
    /// </list>
    /// Every tick is guarded: one bad tick doesn't take down the supervisor.
    /// </summary>
    private async Task SupervisorLoopAsync()
    {
        while (true)
        {
            var status = ServerStatus;
            try
            {
                switch (status)
                {
                    case ServerState.Running:
                    {
                        IReadOnlyList<ServerModel> snapshot = [];
                        var fetchOk = false;
                        try { snapshot = await GetModelsAsync(CancellationToken.None); fetchOk = true; }
                        catch (Exception ex) { Log.Debug($"model poll fetch failed: {ex.Message}"); /* confirmed via /health below */ }

                        if (fetchOk)
                        {
                            try { _lastModelsSnapshot = snapshot; ModelsChanged?.Invoke(this, snapshot); }
                            catch (Exception ex) { Log.Warn(ex, "ModelsChanged handler threw"); /* a handler error doesn't take down the supervisor */ }

                            if (snapshot.Count > 0)
                                Log.Debug(
                                    $"poll: {snapshot.Count} model(s): {string.Join(", ", snapshot.Select(m => $"{m.Id}={(m.Status ?? "?")}"))}");
                        }
                        else
                        {
                            ApplyPolledStatus(DeriveServerStatus(status, await ProbeHealthAsync(CancellationToken.None)));
                        }
                        break;
                    }
                    case ServerState.Starting:
                    case ServerState.Failed:
                        ApplyPolledStatus(DeriveServerStatus(status, await ProbeHealthAsync(CancellationToken.None)));
                        break;
                    // Stopped: no probe, no transition — see DeriveServerStatus.
                }
            }
            catch (Exception ex) { Log.Warn(ex, "supervisor tick threw"); /* one bad tick doesn't take down the supervisor */ }

            await Task.Delay(status == ServerState.Running ? 500 : 1000);
        }
    }

    // ---- Device probing / model fit ----

    /// <summary>Cached device probe result + timestamp (see <see cref="ProbeDevicesAsync"/>).</summary>
    private (DateTime At, DeviceProbe? Probe) _devicesCache;

    /// <summary>
    /// Probes the compute devices available to llama.cpp by running
    /// <c>llama cli --list-devices</c> (see <see cref="DeviceQuery"/>). An
    /// empty list means no accelerator devices — callers fall back to system
    /// CPU/RAM (<see cref="SystemMemory"/>) for fit decisions. The result is
    /// cached for a short window: the probe spawns a process, preflight
    /// checks can come in bursts, and free-VRAM numbers don't need to be
    /// fresher than a minute. A failed probe returns an empty list — it
    /// must never throw or block (fail-open convention).
    /// </summary>
    public async Task<IReadOnlyList<LlamaDevice>> ListDevicesAsync(CancellationToken cancel = default)
    {
        var probe = await ProbeDevicesAsync(cancel);
        var selection = InferenceRuntime.SelectDevice(RuntimeBackend, GpuDeviceName, probe);
        if (selection.DeviceArgument is null) return probe.Devices;
        if (selection.DeviceArgument == "none") return [];
        var selected = selection.DeviceArgument.Split(',');
        return probe.Devices.Where(device => selected.Contains(device.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// The full probe result, including whether the probe succeeded — see
    /// <see cref="DeviceProbe"/>. Callers that only budget memory can use
    /// <see cref="ListDevicesAsync"/> (fail-open list); the footer's device
    /// indicator needs the success flag to tell "CPU-only machine" apart
    /// from "probe failed / binary not resolved yet".
    /// </summary>
    public async Task<DeviceProbe> ProbeDevicesAsync(CancellationToken cancel = default)
    {
        const double CacheTtlSeconds = 60;

        var cache = _devicesCache;
        if (cache.Probe is not null &&
            (DateTime.UtcNow - cache.At).TotalSeconds < CacheTtlSeconds)
            return cache.Probe;

        var binary = BinaryPath;
        if (binary is null || !File.Exists(binary))
            binary = FindOnPath("llama.exe");
        if (binary is null)
        {
            Log.Debug("list-devices skipped: no llama binary resolved yet");
            return new DeviceProbe(false, []);
        }

        var probe = await DeviceQuery.ListDevicesAsync(binary, cancel);
        _devicesCache = (DateTime.UtcNow, probe);
        Log.Info(probe.Devices.Count == 0
            ? probe.Succeeded
                ? "list-devices: no accelerator devices (CPU/RAM fallback)"
                : "list-devices: probe failed (unreadable output)"
            : $"list-devices: {string.Join(", ", probe.Devices.Select(d => $"{d.Id} '{d.Name}' {d.FreeBytes / (1 << 20)} MiB free"))}");
        return probe;
    }

    /// <summary>
    /// Decides whether a model can run on this machine BEFORE it is
    /// downloaded or loaded — from the catalog metadata (parameter count,
    /// quant, GGUF file size) against a live device probe
    /// (<see cref="ListDevicesAsync"/>) and system RAM
    /// (<see cref="SystemMemory"/>). See <see cref="MemoryFit"/> for the
    /// decision rules. Never throws; unknown inputs fail open.
    /// </summary>
    /// <param name="parameterCount">Catalog params label, e.g. <c>"20B"</c>,
    /// <c>"26B-A4B"</c>, <c>"E4B"</c> — empty/null when unknown.</param>
    /// <param name="quant">Quant label, e.g. <c>"Q4_K_M"</c>, <c>"mxfp4"</c>.</param>
    /// <param name="fileSizeBytes">GGUF size in bytes (0 when unknown) — the
    /// best weight-size signal when present.</param>
    public async Task<MemoryFitResult> CheckModelFitAsync(
        string? parameterCount, string? quant, ulong fileSizeBytes,
        CancellationToken cancel = default)
    {
        var devices = await ListDevicesAsync(cancel);
        ulong? availableRam = SystemMemory.TryGet(out _, out var avail) ? avail : null;
        var estimate = ModelMemoryEstimator.Estimate(parameterCount, quant, fileSizeBytes);
        var result = MemoryFit.Check(estimate, devices, availableRam);
        Log.Info($"fit check: params={parameterCount ?? "<none>"} quant={quant ?? "<none>"} " +
            $"file={fileSizeBytes}B → fits={result.Fits} target={result.Target} ({result.Details})");
        return result;
    }

    /// <summary>
    /// Cached fit-params probe results + timestamps, keyed by (model path,
    /// context tokens) — see <see cref="QueryFitParamsAsync"/>.
    /// </summary>
    private readonly Dictionary<(string Path, int Ctx, string CacheK, string CacheV),
        (DateTime At, FitParamsEstimate? Estimate)> _fitParamsCache = new();

    /// <summary>
    /// Asks the CLI's <c>fit-params</c> tool for llama.cpp's own memory
    /// estimate of an on-disk model at a context length (see
    /// <see cref="FitParamsQuery"/>) — the accurate, GPU-aware counterpart
    /// of the catalog-metadata heuristic (<see cref="CheckModelFitAsync"/>)
    /// used once the GGUF is actually present. <c>null</c> means "no
    /// verdict" (no binary, CPU-era build without the tool, unreadable
    /// model) — callers keep their heuristic then, never block. Results are
    /// cached per (path, context) for a short window: the context-length
    /// picker probes a burst of options when it opens, and the estimate
    /// (unlike free VRAM) doesn't change between them.
    /// </summary>
    public async Task<FitParamsEstimate?> QueryFitParamsAsync(
        string modelPath, int contextTokens, CancellationToken cancel = default,
        string cacheTypeK = "f16", string cacheTypeV = "f16")
    {
        const double CacheTtlSeconds = 60;

        var key = (modelPath, contextTokens, cacheTypeK, cacheTypeV);
        lock (_fitParamsCache)
        {
            if (_fitParamsCache.TryGetValue(key, out var cached) &&
                (DateTime.UtcNow - cached.At).TotalSeconds < CacheTtlSeconds)
                return cached.Estimate;
        }

        var binary = BinaryPath;
        if (binary is null || !File.Exists(binary))
            binary = FindOnPath("llama.exe");
        if (binary is null)
        {
            Log.Debug("fit-params skipped: no llama binary resolved yet");
            return null;
        }

        var estimate = await FitParamsQuery.QueryAsync(binary, modelPath, contextTokens,
            cancel, cacheTypeK, cacheTypeV);
        lock (_fitParamsCache)
        {
            _fitParamsCache[key] = (DateTime.UtcNow, estimate);
        }
        Log.Debug(estimate is null
            ? $"fit-params: no verdict for {Path.GetFileName(modelPath)} at ctx {contextTokens}"
            : $"fit-params: {Path.GetFileName(modelPath)} at ctx {contextTokens} needs " +
              $"{MemoryFit.FormatBytes(estimate.TotalBytes)} ({estimate.Devices.Count} device(s) + host)");
        return estimate;
    }

    /// <summary>
    /// The memory budget a context-length option is compared against:
    /// the sum of free VRAM across all accelerator devices plus the usable
    /// share of free system RAM (<see cref="MemoryFit.CpuRamBudgetFraction"/>).
    /// The budgets are <b>added</b>, not tried one after the other as in
    /// <see cref="MemoryFit"/>, because a loaded model can split its layers
    /// across VRAM and RAM (partial offload) — so a context option fits when
    /// its total requirement fits the combined pool. Fails open exactly like
    /// the fit checks: no devices AND an unknown RAM probe returns
    /// <see cref="ulong.MaxValue"/> (gray nothing out on a measurement
    /// error); with devices but no RAM number, the VRAM sum alone stands.
    /// </summary>
    public async Task<ulong> ContextMemoryBudgetAsync(CancellationToken cancel = default)
    {
        var devices = await ListDevicesAsync(cancel);
        var vram = MemoryFit.SumFreeVram(devices);

        if (!SystemMemory.TryGet(out _, out var availableRam))
            return devices.Count > 0 ? vram : ulong.MaxValue;

        return vram + (ulong)(availableRam * MemoryFit.CpuRamBudgetFraction);
    }

    // ---- Model download ----

    /// <summary>
    /// Downloads a model by asking the running llama server to fetch it. The
    /// server (router mode) handles the actual Hugging Face transfer; this
    /// method just <b>POST</b>s <c>{"model": "&lt;name&gt;"}</c> to
    /// <c>/models</c> and tracks progress via the <c>/models/sse</c> stream.
    /// <para>Flow:
    /// <list type="number">
    /// <item>Open an SSE connection to <c>/models/sse</c> and start parsing
    /// events.</item>
    /// <item>POST the model name to <c>/models</c> — the server kicks off the
    /// download and emits <c>download_progress</c> SSE events.</item>
    /// <item>Sum the per-URL <c>done</c>/<c>total</c> bytes from each progress
    /// event and report them via <paramref name="progress"/>.</item>
    /// <item>Complete (return) when a <c>download_finished</c> or
    /// <c>download_failed</c> event arrives for the model.</item>
    /// </list></para>
    /// </summary>
    /// <param name="model">The model to download; <see cref="IModel.ServerModelId"/>
    /// is the Hugging Face repo id with its <c>:&lt;quant&gt;</c> suffix (e.g.
    /// <c>ggml-org/gpt-oss-20b-GGUF:Q4_0</c>). The quant suffix is what tells
    /// the server which GGUF variant to fetch — a bare repo id leaves the
    /// choice to the server's own default resolution, which can download a
    /// different (typically larger) quant than the one the user picked.</param>
    /// <param name="progress">Receives <see cref="ModelDownloadProgress"/> updates
    /// as the server streams them. May be <c>null</c>.</param>
    /// <param name="cancel">Cancels the download (closes the SSE stream and
    /// asks the server to stop via <c>POST /models/unload</c> — see
    /// <c>CancelServerDownloadAsync</c> for why not DELETE).</param>
    /// <returns><c>true</c> if the download finished successfully;
    /// <c>false</c> on failure or cancellation.</returns>
    public async Task<bool> DownloadModelAsync(IModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        Log.Info($"downloading model {model.Name}");
        if (ServerStatus != ServerState.Running)
        {
            progress?.Report(new ModelDownloadProgress(
                model.Name, 0, 0, Done: false, Failed: true, Message: "Server is not running"));
            return false;
        }

        // The canonical repo:quant id — the quant suffix pins the exact GGUF
        // variant the user picked; POSTing a bare repo id lets the server
        // resolve its own default quant (observed: Q4_0 requested, Q8_0 landed
        // in the cache).
        var modelName = model.ServerModelId;

        // Open the SSE stream first so we don't miss the earliest progress events.
        // HttpCompletionOption.ResponseHeadersRead lets us read the body as it
        // arrives rather than buffering the whole (infinite) stream.
        // `using` so the response (and its underlying connection / Content stream)
        // is released on EVERY exit path — the early returns from a POST failure
        // and the throw on cancellation used to skip the only Dispose() call,
        // leaking an HTTP connection per failed/canceled download.
        using var sseResponse = await _http.GetAsync(
            "/models/sse",
            HttpCompletionOption.ResponseHeadersRead,
            cancel);
        sseResponse.EnsureSuccessStatusCode();

        // Read the SSE stream on a background task; it feeds events into a
        // channel we consume below. This decouples line-by-line parsing from
        // the POST + completion logic.
        var stream = await sseResponse.Content.ReadAsStreamAsync(cancel);
        var reader = new StreamReader(stream);

        using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        // POST the model name to /models — the server starts the download.
        var payload = BuildModelRequestBody(modelName);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var budget = WithTimeout(TimeSpan.FromSeconds(30), cancel);
            using var postResp = await _http.PostAsync("/models", content, budget.Token);
            if (!postResp.IsSuccessStatusCode)
            {
                var body = await postResp.Content.ReadAsStringAsync(cancel);
                await sseCts.CancelAsync();
                progress?.Report(new ModelDownloadProgress(
                    modelName, 0, 0, Done: false, Failed: true,
                    Message: $"Server rejected the request ({(int)postResp.StatusCode}): {body}",
                    HttpStatus: (int)postResp.StatusCode));
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "download POST threw");
            await sseCts.CancelAsync();
            progress?.Report(new ModelDownloadProgress(
                modelName, 0, 0, Done: false, Failed: true, Message: ex.Message,
                ExceptionType: ex.GetType().Name));
            return false;
        }

        // Consume SSE events until the download finishes or fails for our model.
        // ParseSseStreamAsync is an async iterator that yields events as they
        // arrive from the stream — no Task.Run needed since IAsyncEnumerable is
        // inherently lazy/streaming.
        //
        // On completion, we `break` out of the loop rather than cancelling the
        // SSE stream in-place: cancelling sseCts mid-iteration would make the
        // next ReadLineAsync throw OperationCanceledException, and since that
        // exception comes from sseCts (not the user's `cancel` token) it would
        // escape the `when (cancel.IsCancellationRequested)` guard below and
        // propagate out of the method — masking a successful download as a
        // cancellation and skipping the post-download load. Breaking lets the final
        // block cancel + dispose the stream cleanly with no thrown exception.
        var success = false;
        var completed = false;
        try
        {
            await foreach (var (evt, modelId, data) in ParseSseStreamAsync(reader, sseCts.Token))
            {
                cancel.ThrowIfCancellationRequested();
                // The SSE "model" field may carry the bare repo or the
                // repo:quant form depending on the server build and how the
                // download was started — match on the repo part so neither
                // form is missed for this download.
                if (!SameDownloadModel(modelId, modelName))
                    continue; // another model's event

                switch (evt)
                {
                    case "download_progress":
                        var (downloaded, total) = SumProgress(data);
                        progress?.Report(new ModelDownloadProgress(
                            modelName, downloaded, total, Done: false, Failed: false));
                        break;

                    case "download_finished":
                        success = true;
                        progress?.Report(new ModelDownloadProgress(
                            modelName, 0, 0, Done: true, Failed: false, Message: "Download complete"));
                        completed = true;
                        break;

                    case "download_failed":
                        // Capture the server's real error payload (a string
                        // error/message field) instead of discarding it — the
                        // raw payload is logged verbatim and, when present,
                        // carried in Message for classification/diagnosis.
                        // llama.cpp master currently sends data:{} with no
                        // error field, so fall back to the existing literal.
                        var detail = ExtractFailureDetail(data);
                        var rawPayload = data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText();
                        Log.Warn($"server reported download_failed for {modelName}; data: {rawPayload}");
                        progress?.Report(new ModelDownloadProgress(
                            modelName, 0, 0, Done: false, Failed: true,
                            Message: detail ?? "Download failed"));
                        completed = true;
                        break;
                }

                if (completed) break; // exit to await foreach; finally cleans up
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // User canceled — ask the server to stop the download.
            await CancelServerDownloadAsync(modelName);
            progress?.Report(new ModelDownloadProgress(
                modelName, 0, 0, Done: false, Failed: false, Message: "Cancelled"));
            throw;
        }
        finally
        {
            // sseResponse is disposed by its `using` at scope exit; only cancel
            // the linked token here so an in-flight ReadLineAsync unwinds.
            await sseCts.CancelAsync();
        }

        // The stream ended without a terminal event (server dropped the
        // connection mid-download): nothing was reported, so the caller's
        // failure path has no detail to show. Log it so the "check the app
        // log" guidance has substance. No new progress report — the return
        // contract and row-state ownership are unchanged.
        if (!completed && !success)
            Log.Warn($"SSE stream ended before a terminal download event for {modelName}");

        return success;
    }

    /// <summary>
    /// Watches a download the app did <b>not</b> start (e.g. triggered from the
    /// WebUI or the CLI) and reports its byte progress until it finishes. Unlike
    /// <see cref="DownloadModelAsync"/> nothing is POSTed — the download is
    /// already in flight — and cancellation only stops the watch; it never
    /// cancels the server-side download.
    /// <para>There is deliberately no idle timeout: the caller (the <c>/models</c>
    /// poller) owns the watch's lifetime and cancels it as soon as the model
    /// leaves the <c>downloading</c> state, so a quiet stream (a stalled but
    /// living download) is waited out rather than second-guessed.</para>
    /// </summary>
    /// <param name="repoName">The server model id of the download to watch —
    /// the Hugging Face repo id as the server keys it, bare
    /// (<c>ggml-org/gemma-3-4b-it-GGUF</c>) or with its quant suffix
    /// (<c>…GGUF:Q4_0</c>). Either form works: SSE events are matched on the
    /// repo part (see <see cref="SameDownloadModel"/>).</param>
    /// <param name="progress">Receives <see cref="ModelDownloadProgress"/> updates
    /// as the server streams them. May be <c>null</c>.</param>
    /// <param name="cancel">Stops the watch (does not affect the download).</param>
    /// <returns><c>true</c> if the download finished while watching;
    /// <c>false</c> if it failed, the stream ended, or the watch was canceled.</returns>
    public async Task<bool> WatchDownloadAsync(
        string repoName,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
            return false;

        // Same pattern as DownloadModelAsync: the shared long-lived client, the
        // body read as it arrives, and `using` on the response so the connection
        // is released on every exit path. A dead stream degrades to "no
        // progress" — the poller keeps the row's state truthful regardless.
        try
        {
            using var sseResponse = await TryOpenSseAsync(cancel);
            if (sseResponse is null)
                return false;

            using var reader = new StreamReader(await sseResponse.Content.ReadAsStreamAsync(cancel));
            await foreach (var (evt, modelId, data) in ParseSseStreamAsync(reader, cancel))
            {
                // Match on the repo part: the SSE "model" field may or may not
                // carry the quant suffix depending on how (and by whom) the
                // download was started — an exact compare of the bare repo id
                // misses repo:quant events and leaves the row on an
                // indeterminate ring for the whole download.
                if (!SameDownloadModel(modelId, repoName))
                    continue; // another model's event ("*" broadcasts carry no progress)

                switch (evt)
                {
                    case "download_progress":
                        var (downloaded, total) = SumProgress(data);
                        progress?.Report(new ModelDownloadProgress(
                            repoName, downloaded, total, Done: false, Failed: false));
                        break;

                    case "download_finished":
                        progress?.Report(new ModelDownloadProgress(
                            repoName, 0, 0, Done: true, Failed: false, Message: "Download complete"));
                        return true;

                    case "download_failed":
                        // Same capture as DownloadModelAsync: keep the server's
                        // real error payload (logged verbatim) rather than
                        // discarding it. Row-state transitions stay with the
                        // poller — this only enriches the report.
                        var watchDetail = ExtractFailureDetail(data);
                        var watchRawPayload = data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText();
                        Log.Warn($"server reported download_failed for {repoName}; data: {watchRawPayload}");
                        progress?.Report(new ModelDownloadProgress(
                            repoName, 0, 0, Done: false, Failed: true,
                            Message: watchDetail ?? "Download failed"));
                        return false;
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The poller canceled the watch (download completed, failed, or
            // vanished) — not an error, and the download itself is deliberately
            // left alone.
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            // ObjectDisposedException covers the race where the poller cancels
            // and disposes the token source before the first GetAsync registers
            // the token.
            Log.Warn(ex, $"download watch for {repoName} ended early");
        }

        return false;
    }

    /// <summary>
    /// Asks the running llama server to load (launch) a model into memory via
    /// <c>POST /models/load</c>. In router mode, the server spawns a child
    /// process for the model.
    /// </summary>
    /// <param name="model">The model to load; <see cref="Common.IModel.ServerModelId"/>
    /// is the canonical id the server knows (the HF repo id with its
    /// <c>:&lt;quant&gt;</c> suffix, e.g. <c>ggml-org/gemma-3-4b-it-GGUF:Q4_K_M</c>) —
    /// <c>/models/load</c> requires the quant suffix, so the bare repo id won't do.</param>
    /// <param name="progress">Optional sink for the load fraction (0..1). When
    /// provided, the <c>/models/sse</c> stream is opened BEFORE the POST (a small
    /// model can finish loading in under a second — opening it after would miss
    /// the whole load) and <c>status_change</c> events are watched until the
    /// model reaches a terminal state, reporting each event's fraction. When
    /// <c>null</c>, the method is fire-and-forget: it returns as soon as the
    /// load request is accepted.</param>
    /// <param name="contextLengthTokens">Optional context size (<c>ctx_size</c>)
    /// for the spawned model process — the per-model preference chosen in the
    /// model details view. <c>null</c> lets the server use its own default. Unknown
    /// JSON fields are ignored by the server's body parser, so an older llama.cpp
    /// that predates per-model load options simply loads with its default.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns><c>false</c> only when the load definitely didn't happen — the
    /// POST was rejected, or the server rolled the model back to <c>unloaded</c>
    /// (a failed load). <c>true</c> means accepted; the <see cref="ModelsChanged"/>
    /// poller confirms the final <c>loaded</c> transition.</returns>
    public async Task<bool> LoadModelAsync(IModel model, IProgress<double>? progress = null, int? contextLengthTokens = null, CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
            return false;

        var modelId = model.ServerModelId;

        // Open the SSE stream before the POST when progress is wanted. If it
        // can't be opened (older server without /models/sse), degrade to
        // fire-and-forget — the /models poller still reconciles the state.
        using var sseResponse = progress is null ? null : await TryOpenSseAsync(cancel);
        using var reader = sseResponse is null
            ? null
            : new StreamReader(await sseResponse.Content.ReadAsStreamAsync(cancel));

        try
        {
            Log.Info($"loading model {modelId}" +
                (contextLengthTokens is { } ctx ? $" (ctx_size={ctx})" : ""));
            var payload = contextLengthTokens is { } ctxSize
                ? BuildModelRequestBody(modelId, ctxSize)
                : BuildModelRequestBody(modelId);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var budget = WithTimeout(TimeSpan.FromSeconds(30), cancel);
            using var resp = await _http.PostAsync("/models/load", content, budget.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"server rejected model load ({(int)resp.StatusCode})");
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "model load request threw");
            return false;
        }

        if (reader is null || progress is null)
            return true; // accepted; the poller takes it from here

        // Watch status_change events until the model reaches a terminal state.
        // The timeout only guards against a server that never sends one (a hung
        // child process): the request WAS accepted, so a timeout still returns
        // true and leaves the state to the poller.
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        watchCts.CancelAfter(LoadWatchTimeout);
        try
        {
            await foreach (var (evt, evtModel, data) in ParseSseStreamAsync(reader, watchCts.Token))
            {
                if (evt != "status_change" ||
                    !string.Equals(evtModel, modelId, StringComparison.OrdinalIgnoreCase))
                    continue; // another model's event

                var (status, fraction) = ParseStatusChange(data);
                switch (status)
                {
                    case "loading":
                        progress.Report(fraction);
                        break;
                    case "loaded":
                        progress.Report(1.0);
                        return true;
                    case "unloaded":
                        // Rolled back — the load failed server-side (e.g. the
                        // child process died while mapping the weights).
                        Log.Warn($"load of {modelId} rolled back to unloaded");
                        return false;
                }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Log.Warn($"timed out waiting for load events for {modelId}");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            // The server died mid-load: its SSE stream broke, so the load
            // definitely didn't complete — report failure so the caller drops
            // the row back to the play glyph instead of spinning forever.
            Log.Warn(ex, $"load watch for {modelId} broke (server died?)");
            return false;
        }

        return true;
    }

    // How long the load-progress SSE watch waits for a terminal status_change
    // before deferring to the /models poller. Generous because mapping a very
    // large model from a slow disk can take minutes.
    private static readonly TimeSpan LoadWatchTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Opens the <c>/models/sse</c> event stream for progress watching.
    /// Returns <c>null</c> (and logs) when the stream can't be opened — the
    /// caller then degrades to poller-only state tracking.
    /// </summary>
    private async Task<HttpResponseMessage?> TryOpenSseAsync(CancellationToken cancel)
    {
        try
        {
            var resp = await _http.GetAsync(
                "/models/sse", HttpCompletionOption.ResponseHeadersRead, cancel);
            if (resp.IsSuccessStatusCode) return resp;
            
            Log.Warn($"SSE stream rejected ({(int)resp.StatusCode}); progress falls back to the poller");
            resp.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(ex, "SSE stream unavailable; progress falls back to the poller");
            return null;
        }
    }

    /// <summary>
    /// Asks the running llama server to unload a model from memory via
    /// <c>POST /models/unload</c>. In router mode, the server stops the model's
    /// child process; this returns once the unload request is accepted. Track
    /// the transition via the <see cref="ModelsChanged"/> poller, which reports the
    /// server's <see cref="ServerModel.Status"/> field flipping from <c>loaded</c>
    /// to <c>unloaded</c>.
    /// </summary>
    /// <param name="model">The model to unload; <see cref="Common.IModel.ServerModelId"/>
    /// is the canonical id the server knows.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns><c>true</c> if the server accepted the unload request.</returns>
    public async Task<bool> UnloadModelAsync(IModel model, CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
            return false;

        try
        {
            Log.Info($"unloading model {model.ServerModelId}");

            var payload = BuildModelRequestBody(model.ServerModelId);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var budget = WithTimeout(TimeSpan.FromSeconds(30), cancel);
            using var resp = await _http.PostAsync("/models/unload", content, budget.Token);
            if (!resp.IsSuccessStatusCode)
                Log.Warn($"server rejected model unload ({(int)resp.StatusCode})");
            
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "model unload request threw");
            return false;
        }
    }

    // ---- Model listing (GET /models) ----

    /// <summary>
    /// Removes a cached model through the running server via
    /// <c>DELETE /models?model={name}</c> (the model name is passed as a query
    /// param, not in the path — only cached, non-preset models can be deleted).
    /// The server deletes the on-disk GGUF and drops it from the model list;
    /// the <see cref="ModelsChanged"/> poller will surface the removal on its
    /// next tick (the server also emits a <c>model_remove</c> SSE event).
    /// Added-folder models are presets, so their selected GGUF file is deleted
    /// locally after checking the server reports it unloaded; presets are then
    /// rebuilt and reloaded. Only files rediscovered in configured roots qualify.
    /// Returns <c>false</c> (without throwing) when the server isn't running or
    /// rejects the request.
    /// </summary>
    /// <param name="model">The model to delete; <see cref="Common.IModel.ServerModelId"/>
    /// is the canonical id the server knows.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns><c>true</c> if the server accepted the delete request.</returns>
    public async Task<bool> DeleteModelAsync(IModel model, CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
        {
            Log.Warn("cannot delete model while the server is not running");
            return false;
        }

        try
        {
            Log.Info($"deleting model {model.ServerModelId}");
            if (model.ServerModelId.StartsWith("local/", StringComparison.Ordinal))
            {
                using var localBudget = WithTimeout(TimeSpan.FromSeconds(30), cancel);
                using var listing = await _http.GetAsync("/models", localBudget.Token);
                listing.EnsureSuccessStatusCode();
                await using var stream = await listing.Content.ReadAsStreamAsync(localBudget.Token);
                var dto = await JsonSerializer.DeserializeAsync<ModelsResponseDto>(
                    stream, cancellationToken: localBudget.Token);
                var current = dto?.Data?.FirstOrDefault(d => d.Id == model.ServerModelId);
                if (current is null ||
                    !string.Equals(current.Status?.Value, "unloaded", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only an unloaded added-folder model can be deleted.");

                await Task.Run(() => LocalModelDirectory.Delete(
                    model.ServerModelId, AdditionalModelDirectories), localBudget.Token);
                // Preset models cannot be deleted through DELETE /models.
                await _additionalModels.RefreshAsync(AdditionalModelDirectories, cancel, force: true);
                WriteModelPresetsIni(ActiveDeviceId, _lastDeviceProbe);
                using var reload = await _http.GetAsync("/models?reload=1", localBudget.Token);
                reload.EnsureSuccessStatusCode();
                await using var reloadedStream = await reload.Content.ReadAsStreamAsync(localBudget.Token);
                var reloaded = await JsonSerializer.DeserializeAsync<ModelsResponseDto>(
                    reloadedStream, cancellationToken: localBudget.Token);
                if (reloaded?.Data is null || reloaded.Data.Any(d => d.Id == model.ServerModelId))
                    throw new IOException("The file was deleted, but the server did not remove its preset. Restart Llama.");
                return true;
            }
            var url = $"/models?model={Uri.EscapeDataString(model.ServerModelId)}";
            using var budget = WithTimeout(TimeSpan.FromSeconds(30), cancel);
            using var resp = await _http.DeleteAsync(url, budget.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"server rejected model delete ({(int)resp.StatusCode})");
            }
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            Log.Warn(ex, "model delete request timed out");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "model delete request threw");
            return false;
        }
    }

    /// <summary>
    /// A model the running llama server knows about (router <c>/models</c> list):
    /// its canonical <see cref="Id"/> (<c>repo:quant</c>), on-disk <see cref="Path"/>,
    /// load <see cref="Status"/> (<c>loaded</c>/<c>unloaded</c>), and the
    /// <see cref="SupportsImage"/> flag derived from
    /// <c>architecture.input_modalities</c>.
    /// </summary>
    public sealed record ServerModel
    {
        /// <summary>Server model id, e.g. <c>ggml-org/gemma-3-4b-it-GGUF:Q4_K_M</c>.</summary>
        public string Id { get; init; } = "";
        /// <summary>Absolute path to the GGUF on disk, when known.</summary>
        public string? Path { get; init; }
        /// <summary>Load state reported by the server: <c>unloaded</c>, <c>downloading</c>, <c>loading</c>, <c>loaded</c>, or <c>sleeping</c>.</summary>
        public string Status { get; init; } = "";
        /// <summary>True when <see cref="Status"/> is <c>loaded</c> (model resident in a child process)
        /// or <c>sleeping</c> (freed after the idle timeout but still the active model — it wakes
        /// transparently on the next request, so for every caller it counts as loaded).</summary>
        public bool IsLoaded =>
            string.Equals(Status, "loaded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "sleeping", StringComparison.OrdinalIgnoreCase);
        /// <summary>True when <see cref="Status"/> is <c>sleeping</c>: the server freed the model's
        /// memory after <c>--sleep-idle-seconds</c> of idleness; the next request wakes it.</summary>
        public bool IsSleeping => string.Equals(Status, "sleeping", StringComparison.OrdinalIgnoreCase);
        /// <summary>True when <see cref="Status"/> is <c>loading</c> (load in progress: child process spawning / weights mmapping).</summary>
        public bool IsLoading => string.Equals(Status, "loading", StringComparison.OrdinalIgnoreCase);
        /// <summary>True when <see cref="Status"/> is <c>downloading</c> (the server is fetching the
        /// model's files; the model is id'd by its bare repo until the download completes and the
        /// quant is resolved).</summary>
        public bool IsDownloading => string.Equals(Status, "downloading", StringComparison.OrdinalIgnoreCase);
        /// <summary>True when <c>architecture.input_modalities</c> contains <c>image</c>.</summary>
        public bool SupportsImage { get; init; }
        /// <summary>All declared input modalities (e.g. <c>text</c>, <c>image</c>).</summary>
        public IReadOnlyList<string> InputModalities { get; init; } = [];
        /// <summary>Where the server found the model, e.g. <c>cache</c>.</summary>
        public string? Source { get; init; }
        /// <summary>Whether the server allows removing this model.</summary>
        public bool CanRemove { get; init; }
    }

    /// <summary>
    /// Fetches the server's model list (<c>GET /models</c>) — the authoritative
    /// set of locally available (cached) models, with each model's load state and
    /// architecture (vision capability). Returns an empty list when the server
    /// isn't running or the request fails.
    /// </summary>
    public async Task<IReadOnlyList<ServerModel>> GetModelsAsync(CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
            return [];

        try
        {
            // Adoption skips managed launch; initialize folder paths for its rows too.
            await _additionalModels.RefreshAsync(AdditionalModelDirectories, cancel);
            using var budget = WithTimeout(TimeSpan.FromSeconds(10), cancel);
            using var resp = await _http.GetAsync("/models", budget.Token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
            var dto = await JsonSerializer.DeserializeAsync<ModelsResponseDto>(stream, cancellationToken: cancel);
            return dto?.Data?.Select(d =>
            {
                var model = Map(d);
                return _additionalModels.Snapshot.TryGetValue(model.Id, out var path)
                    ? model with { Path = path, Source = "local", CanRemove = true }
                    : model;
            }).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    internal static ServerModel Map(ServerModelDto d) => new()
    {
        Id = d.Id ?? "",
        Path = d.Path,
        Status = d.Status?.Value ?? "",
        SupportsImage = d.Architecture?.InputModalities != null
            && d.Architecture.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
        InputModalities = (IReadOnlyList<string>?)d.Architecture?.InputModalities ?? [],
        Source = d.Source,
        CanRemove = d.CanRemove,
    };

    // ---- Chat completion (POST /v1/chat/completions, SSE) ----

    /// <summary>
    /// The model the spotlight overlay should prompt: the first server-reported
    /// <c>loaded</c> model, or <c>null</c> when none is resident (the overlay
    /// shows its disabled hint in that case). Cached from the latest poller
    /// snapshot, so a hotkey press doesn't block on <c>GET /models</c>.
    /// </summary>
    private IReadOnlyList<ServerModel> _lastModelsSnapshot = [];

    /// <summary>Latest known loaded model id, or <c>null</c> when none is loaded.</summary>
    public string? LoadedModelId => (from m in _lastModelsSnapshot where m.IsLoaded select m.Id).FirstOrDefault();

    /// <summary>
    /// The most recent <c>GET /models</c> snapshot (refreshed by the 1s poller
    /// while the server runs). Lets detail views read per-model metadata (e.g.
    /// the on-disk <see cref="ServerModel.Path"/>) without an extra HTTP round
    /// trip; may be empty or one poll cycle stale.
    /// </summary>
    public IReadOnlyList<ServerModel> LastModelsSnapshot => _lastModelsSnapshot;

    /// <summary>
    /// Streams an OpenAI-compatible chat completion for <paramref name="userMessage"/>
    /// against the currently loaded model, yielding <c>delta.content</c> chunks as
    /// they arrive from <c>POST /v1/chat/completions</c> (SSE). Throws if the
    /// server isn't running or no model is loaded. The caller cancels to abort.
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        if (ServerStatus != ServerState.Running)
            throw new InvalidOperationException("llama server is not running.");

        var model = LoadedModelId
            ?? throw new InvalidOperationException("No model is loaded. Load one from the flyout first.");

        var body = $$"""{"model":{{JsonString(model)}},"stream":true, "return_progress": true, "messages":[{"role":"user","content":{{JsonString(userMessage)}}}]}""";
        // SendAsync with ResponseHeadersRead returns as soon as the response
        // headers arrive, so we can read the SSE body incrementally below.
        // PostAsync (the default ResponseContentRead) would buffer the entire
        // response before completing — defeating streaming and making the
        // overlay hang until the whole generation finished.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        
        Log.Info($"chat completion → POST /v1/chat/completions (model={model}, prompt={userMessage.Length} chars)");
        
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
        Log.Info($"chat completion ← HTTP {(int)resp.StatusCode} {resp.StatusCode}");
        resp.EnsureSuccessStatusCode();

        // Reuse the same SSE line framing as /models/sse: data: {json} lines,
        // terminated by a blank line / "data: [DONE]". We parse incrementally so
        // tokens surface as soon as the server flushes them.
        // The default buffer size matches the working /models/sse path.
        await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(stream);

        var yielded = 0;
        var loggedLines = 0;
        Log.Info("chat completion: reading SSE stream");
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            cancel.ThrowIfCancellationRequested();
            // Log the first few raw lines verbatim (truncated) so we can see
            // the exact framing the server uses — prefix, line breaks, JSON
            // shape. Capped to avoid spamming the log on long generations.
            if (loggedLines < 20)
            {
                var preview = line.Length > 200 ? string.Concat(line.AsSpan(0, 200), "…") : line;
                Log.Debug($"chat sse raw[{loggedLines}]: '{preview}'");
                loggedLines++;
            }
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var value = line[5..].TrimStart();
            if (value == "[DONE]")
            {
                Log.Info($"chat completion done: {yielded} chunk(s) yielded");
                yield break;
            }
            if (value.Length == 0) continue;

            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            
            var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
            if (delta.ValueKind != JsonValueKind.Object ||
                !delta.TryGetProperty("content", out var c) ||
                c.ValueKind != JsonValueKind.String) continue;
            
            var text = c.GetString();
            if (string.IsNullOrEmpty(text)) continue;
           
            yielded++;
            yield return text;
        }
        // ReadLineAsync returned null: the server closed the stream without
        // sending [DONE]. Log so we can tell a hang (no log) from a clean
        // close with zero parsed chunks (this line).
        Log.Info($"chat completion stream ended without [DONE]: {yielded} chunk(s) yielded");
    }

    /// <summary>
    /// Builds a <c>{"model":…}</c> request body (optionally with
    /// <c>ctx_size</c>), JSON-escaping the id. Model ids originate in remote
    /// payloads (Hub search, the catalog, an adopted server's <c>/models</c>),
    /// so they must never be piped raw into a hand-written JSON body — a quote
    /// or backslash would corrupt the request and could inject fields. Pure so
    /// the escaping is unit-testable.
    /// </summary>
    internal static string BuildModelRequestBody(string modelId, int? ctxSize = null)
        => ctxSize is { } ctx
            ? $$"""{"model":{{JsonString(modelId)}},"ctx_size":{{ctx}}}"""
            : $$"""{"model":{{JsonString(modelId)}}}""";

    /// <summary>Minimal JSON string escaper for embedding user text in a raw body.</summary>
    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20) sb.Append($"\\u{(int)ch:X4}");
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ---- /models JSON DTOs ----

    internal sealed class ModelsResponseDto
    {
        [JsonPropertyName("data")] public List<ServerModelDto>? Data { get; init; }
    }

    internal sealed class ServerModelDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("status")] public ModelStatusDto? Status { get; set; }
        [JsonPropertyName("architecture")] public ArchitectureDto? Architecture { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("can_remove")] public bool CanRemove { get; set; }
    }

    internal sealed class ModelStatusDto
    {
        [JsonPropertyName("value")] public string Value { get; set; } = "";
    }

    internal sealed class ArchitectureDto
    {
        [JsonPropertyName("input_modalities")] public List<string>? InputModalities { get; set; }
        [JsonPropertyName("output_modalities")] public List<string>? OutputModalities { get; set; }
    }

    /// <summary>
    /// Parses an SSE stream line-by-line, yielding (<c>event</c>, <c>model</c>,
    /// <c>data</c> JSON) tuples. Standard SSE framing: <c>data:</c> lines carry
    /// the payload, a blank line dispatches the event.
    /// </summary>
    internal static async IAsyncEnumerable<(string Event, string Model, JsonElement Data)> ParseSseStreamAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        var pendingData = new StringBuilder();
        while (!cancel.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancel);
            if (line is null)
            {
                // Stream closed — flush any partially accumulated event.
                foreach (var tuple in FlushAsync())
                    yield return tuple;
                yield break;
            }

            if (line.Length == 0)
            {
                // Blank line = dispatch the accumulated event.
                foreach (var tuple in FlushAsync())
                    yield return tuple;
                continue;
            }

            // Accumulate data: lines (may span multiple for a single event).
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var value = line[5..].TrimStart();
            if (pendingData.Length > 0) pendingData.Append('\n');
            pendingData.Append(value);
            // Ignore event:/id:/retry: lines — the server bundles the event
            // type inside the JSON data payload ("event" field).
        }

        yield break;

        // Dispatch whatever has been accumulated so far as a single event.
        // A well-formed SSE stream terminates every event with a blank line. However,
        //  we also flush on EOF so a trailing event without a final blank
        // line (e.g., a server that dropped the connection mid-event, or a test
        // fixture) is not silently dropped.
        IEnumerable<(string Event, string Model, JsonElement Data)> FlushAsync()
        {
            if (pendingData.Length == 0) yield break;

            var json = pendingData.ToString();
            pendingData.Clear();

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
            var mdl = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            
            // Clone detaches the element from the JsonDocument so callers
            // can safely consume it after the enumerator is disposed.
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;

            if (evt.Length > 0)
                yield return (evt, mdl, data);
        }
    }

    /// <summary>
    /// Sums <c>done</c>/<c>total</c> bytes across all URLs in a
    /// <c>download_progress</c> data payload (a repo can have multiple files).
    /// </summary>
    internal static (long downloaded, long total) SumProgress(JsonElement data)
    {
        long downloaded = 0, total = 0;
        if (data.ValueKind != JsonValueKind.Object) return (0, 0);

        if (!data.TryGetProperty("progress", out var progress) ||
            progress.ValueKind != JsonValueKind.Object) return (downloaded, total);
        
        foreach (var url in progress.EnumerateObject().Where(url => url.Value.ValueKind == JsonValueKind.Object))
        {
            // TryGetInt64 throws InvalidOperationException on a non-Number
            // element (e.g. a string) — it only returns false for numbers
            // that don't fit — so the ValueKind must be checked first.
            if (url.Value.TryGetProperty("done", out var done) &&
                done.ValueKind == JsonValueKind.Number &&
                done.TryGetInt64(out var d))
                downloaded += d;
            if (url.Value.TryGetProperty("total", out var tot) &&
                tot.ValueKind == JsonValueKind.Number &&
                tot.TryGetInt64(out var t))
                total += t;
        }

        return (downloaded, total);
    }

    /// <summary>
    /// Extracts the human-readable error text from a <c>download_failed</c> SSE
    /// event's <c>data</c> payload, tolerating every shape: a string
    /// <c>error</c> field, an <c>error</c> object with a string <c>message</c>,
    /// or a string <c>message</c> field. Returns <c>null</c> when no such field
    /// is present (llama.cpp master currently sends <c>data:{}</c>) — no text
    /// is fabricated. Total: malformed/absent payloads (including a default
    /// <see cref="JsonElement"/>, whose <c>ValueKind</c> is
    /// <c>Undefined</c>) yield <c>null</c> rather than throwing.
    /// </summary>
    internal static string? ExtractFailureDetail(JsonElement data)
    {
        try
        {
            // TryGetProperty throws on a non-Object element, and a default
            // JsonElement (no `data` field) is Undefined — guard on ValueKind
            // first, like SumProgress does.
            if (data.ValueKind != JsonValueKind.Object) return null;

            if (data.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    var text = error.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                else if (error.ValueKind == JsonValueKind.Object &&
                         error.TryGetProperty("message", out var nested) &&
                         nested.ValueKind == JsonValueKind.String)
                {
                    var text = nested.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }

            if (data.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a <c>status_change</c> data payload into the model's new
    /// <c>status</c> (<c>loading</c>/<c>loaded</c>/<c>unloaded</c>) and, for
    /// <c>loading</c>, an overall 0..1 fraction. The server's progress object
    /// carries the load <c>stages</c>, the <c>current</c> stage, and that
    /// stage's 0..1 <c>value</c>; the overall fraction weights the value by
    /// the current stage's position — <c>(stageIndex + value) / stageCount</c>,
    /// which reduces to <c>value</c> for the common single-stage load.
    /// </summary>
    internal static (string status, double fraction) ParseStatusChange(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("status", out var s) ||
            s.ValueKind != JsonValueKind.String)
            return ("", 0);

        var status = s.GetString() ?? "";
        double fraction = 0;

        // TryGetDouble throws InvalidOperationException on a non-Number element
        // (same as TryGetInt64), so the ValueKind guard must come first.
        if (data.TryGetProperty("progress", out var progress) &&
            progress.ValueKind == JsonValueKind.Object &&
            progress.TryGetProperty("value", out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out var value))
        {
            fraction = value;

            // Multi-stage load (e.g. text_model + mmproj): weight the current
            // stage's value by how many stages are already behind it.
            if (progress.TryGetProperty("stages", out var stages) &&
                stages.ValueKind == JsonValueKind.Array &&
                stages.GetArrayLength() > 1 &&
                progress.TryGetProperty("current", out var cur) &&
                cur.ValueKind == JsonValueKind.String)
            {
                var current = cur.GetString();
                var index = -1;
                var i = 0;
                foreach (var stage in stages.EnumerateArray())
                {
                    if (stage.ValueKind == JsonValueKind.String &&
                        string.Equals(stage.GetString(), current, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                    i++;
                }
                if (index >= 0)
                    fraction = (index + value) / stages.GetArrayLength();
            }
        }

        return (status, Math.Clamp(fraction, 0, 1));
    }

    /// <summary>
    /// Whether an SSE event's <c>model</c> field belongs to the download
    /// identified by <paramref name="wantedId"/>: exact match, a <c>*</c>
    /// broadcast, or the same repo part — the server ids a model by its bare
    /// repo while the download is in flight (the quant resolves only on
    /// completion), while <paramref name="wantedId"/> is the repo:quant form
    /// the download was started with.
    /// </summary>
    internal static bool SameDownloadModel(string eventId, string wantedId)
    {
        if (eventId == "*") return true;
        if (string.Equals(eventId, wantedId, StringComparison.OrdinalIgnoreCase)) return true;
        var eventRepo = eventId.Split(':')[0];
        var wantedRepo = wantedId.Split(':')[0];
        return string.Equals(eventRepo, wantedRepo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asks the server to cancel an in-flight download via
    /// <c>POST /models/unload</c>. Best-effort — the server may have already
    /// finished or the request may fail; either way the SSE stream is closed
    /// by the caller's cancellation.
    ///
    /// <para>Why unload, not DELETE: the router routes DELETE at
    /// <c>/models?model=…</c> (a path-form <c>DELETE /models/{name}</c> 404s
    /// and the download runs on), and its DELETE handler also removes the
    /// model entry and blocks until the download child exits — on current
    /// builds that wait can wedge the whole router (all <c>/models</c>
    /// requests stall). <c>/models/unload</c> aborts the download promptly
    /// without the remove-from-cache step, and answers as soon as the state
    /// flips — the transient entry then disappears from <c>/models</c> on the
    /// server's next reload sweep. Note the server drops the partial bytes on
    /// abort, so the next attempt re-downloads from scratch.</para>
    /// </summary>
    public async Task CancelServerDownloadAsync(string modelName)
    {
        try
        {
            // Downloads are keyed by the id they were started with
            // (repo:quant), but the server may also know the entry by its
            // bare repo while the download is in flight — try the full id
            // first, then the bare repo when that misses.
            if (await TryUnloadAsync(modelName)) return;
            var repo = modelName.Split(':')[0];
            if (!string.Equals(repo, modelName, StringComparison.Ordinal))
                await TryUnloadAsync(repo);
        }
        catch { /* Best-effort — don't surface cancel cleanup failures. */ }

        async Task<bool> TryUnloadAsync(string id)
        {
            try
            {
                var payload = BuildModelRequestBody(id);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var budget = WithTimeout(TimeSpan.FromSeconds(10), CancellationToken.None);
                using var resp = await _http.PostAsync("/models/unload", content, budget.Token);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }
    }

    // ---- Resolution ----

    private enum ResolutionKind { Missing, Managed, External }

    private record Resolution(ResolutionKind Kind, string? Path);

    /// <summary>
    /// Resolves the active <c>llama</c> binary with a single <c>which</c>-style
    /// PATH lookup (<see cref="FindOnPath(string)"/>). A hit under the install
    /// dir is the app-managed installation; a hit anywhere else is the user's
    /// own external installation; no hit is <see cref="ResolutionKind.Missing"/>.
    /// </summary>
    private static Resolution Resolve()
    {
        if (FindOnPath("llama.exe") is { } found)
            return new Resolution(
                IsManagedPath(found) ? ResolutionKind.Managed : ResolutionKind.External, found);

        return new Resolution(ResolutionKind.Missing, null);
    }

    /// <summary>
    /// <c>which llama.exe</c>: resolves the absolute path of
    /// <paramref name="exeName"/> over the <b>effective</b> PATH — the process
    /// PATH plus the user and machine PATH read from the registry. The registry
    /// reads are what make a just-installed binary visible: <c>install.ps1</c>
    /// runs as a child process and cannot update our own environment block, so
    /// a PATH entry it adds only shows up in the registry (and in the process
    /// PATH of the next login shell).
    /// </summary>
    private static string? FindOnPath(string exeName) =>
        FindOnPath(
            exeName,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

    /// <summary>
    /// Pure core of <see cref="FindOnPath(string)"/>: searches the three PATH
    /// lists in order (process, user, machine), first hit wins, directories
    /// deduped case-insensitively, quoted/whitespace-padded entries normalized,
    /// malformed entries skipped. Internal for tests.
    /// </summary>
    internal static string? FindOnPath(string exeName, string? processPath, string? userPath, string? machinePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pathEnv in new[] { processPath, userPath, machinePath })
        {
            if (string.IsNullOrEmpty(pathEnv)) continue;
            foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = rawDir.Trim().Trim('"');
                if (dir.Length == 0 || !seen.Add(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* Malformed PATH entry — skip. */ }
            }
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="binaryPath"/> sits directly in the app-managed
    /// install dir (<see cref="ManagedInstallDir"/>) — i.e., it's the
    /// installation <c>install.ps1</c> produced, not the user's own.
    /// </summary>
    internal static bool IsManagedPath(string binaryPath) =>
        string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(binaryPath)!),
            Path.GetFullPath(ManagedDir),
            StringComparison.OrdinalIgnoreCase);

    // ---- Install ----

    /// <summary>
    /// Absolute path of Windows PowerShell 5.1 — the interpreter the install
    /// script runs under. Internal so tests can pin the invariant.
    /// </summary>
    internal static string PowerShellExePath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Downloads <see cref="InstallScriptUrl"/> and runs it with PowerShell
    /// (<c>-ExecutionPolicy Bypass -File</c>). The download is pinned to the
    /// exact HTTPS URL and bounded in size (see <see cref="InstallScriptIntegrity"/>);
    /// redirects are refused rather than followed, and the body's SHA-256 is
    /// logged so an install is auditable after the fact. When
    /// <paramref name="allowUnpinned"/> is false (the unattended weekly
    /// update) the script must also match
    /// <see cref="InstallScriptIntegrity.PinnedSha256"/> — otherwise it is
    /// refused rather than executed unverified. Throws on any integrity
    /// failure, a non-zero exit code, or a download failure.
    /// </summary>
    private static async Task DownloadAndRunInstallerAsync(string backend, bool allowUnpinned, CancellationToken cancel)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"llama-install-{Guid.NewGuid():N}.ps1");
        try
        {
            byte[] scriptBytes;

            // Deliberately NOT the shared _http: this is an internet download
            // (llama.app), not a loopback call — the system proxy is welcome
            // here, and the BaseAddress wouldn't apply. Auto-redirect is off
            // so a 30x is surfaced (and refused) instead of silently followed
            // to another host.
            using (var handler = new SocketsHttpHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent.Value);

                // One 30s budget across headers AND body: with
                // ResponseHeadersRead, the HttpClient timeout only covers the
                // headers, and a hostile trickling endpoint could otherwise
                // stretch the body read indefinitely (bytes are capped, time
                // wasn't).
                using var budget = WithTimeout(TimeSpan.FromSeconds(30), cancel);
                using var resp = await client.GetAsync(
                    InstallScriptUrl, HttpCompletionOption.ResponseHeadersRead, budget.Token);

                var responseVerdict = InstallScriptIntegrity.ValidateResponse(
                    (int)resp.StatusCode, resp.RequestMessage?.RequestUri, resp.Content.Headers.ContentLength);
                if (!InstallScriptIntegrity.IsOk(responseVerdict))
                    throw new IOException(
                        $"Refusing to run install.ps1: {InstallScriptIntegrity.Describe(responseVerdict)}.");

                await using var stream = await resp.Content.ReadAsStreamAsync(budget.Token);
                scriptBytes = await InstallScriptIntegrity.ReadBoundedAsync(stream, cancel: budget.Token);
            }

            var sha256 = InstallScriptIntegrity.Sha256Hex(scriptBytes);
            // Logged so a failed install can be correlated with exactly what
            // ran — the script file itself is deleted on the way out.
            Log.Info($"install.ps1 downloaded from {InstallScriptUrl}: {scriptBytes.Length} bytes, sha256={sha256}");

            var hashVerdict = InstallScriptIntegrity.ValidateHash(sha256);
            if (hashVerdict == InstallScriptVerdict.HashMismatch ||
                (hashVerdict == InstallScriptVerdict.Unpinned && !allowUnpinned))
            {
                throw new IOException(
                    $"Refusing to run install.ps1: {InstallScriptIntegrity.Describe(hashVerdict)}.");
            }

            await File.WriteAllBytesAsync(scriptPath, scriptBytes, cancel);

            var psi = new ProcessStartInfo
            {
                // Absolute path: with UseShellExecute=false a bare
                // "powershell.exe" resolves through the CreateProcess search
                // order (the app's directory and the CWD before %SystemRoot%),
                // so a planted executable in either would run the install
                // script regardless of every hash/URL/size check above.
                FileName = PowerShellExePath,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Bypass the per-process execution policy so the downloaded script can
            // run without changing the machine/user policy. The script is fetched
            // over HTTPS from the official llama.app endpoint.
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            if (InferenceRuntime.Normalize(backend) != InferenceRuntime.Automatic)
            {
                // A process-level SKIP_* value must not override an explicit choice.
                foreach (var name in new[] { "SKIP_CUDA", "SKIP_ROCM", "SKIP_VULKAN" })
                    psi.EnvironmentVariables.Remove(name);
            }
            foreach (var (name, value) in InferenceRuntime.InstallerEnvironment(backend))
                psi.EnvironmentVariables[name] = value;

            Log.Info($"running install.ps1 from {InstallScriptUrl} (backend: {InferenceRuntime.Normalize(backend)})");
            using var proc = new Process();
            proc.StartInfo = psi;
            if (!proc.Start())
                throw new InvalidOperationException("Could not start the install script.");

            // Stream output to debug traces for diagnostics; not surfaced to the UI.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancel);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancel);
            await proc.WaitForExitAsync(cancel);
            Log.Debug($"install.ps1 exit code {proc.ExitCode}");

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            
            if (stdout.Length > 0) Log.Debug($"install.ps1 stdout: {stdout.Trim()}");
            if (stderr.Length > 0) Log.Debug($"install.ps1 stderr: {stderr.Trim()}");
            if (proc.ExitCode != 0)
                throw new IOException($"install.ps1 exited with code {proc.ExitCode}.\n{stderr}");
            
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- Version ----

    /// <summary>
    /// Reads the binary's version string by running <c>llama --version</c> and
    /// capturing the first non-empty line. Returns <c>null</c> if it can't be
    /// read (the server still runs with an unreadable version — fail open).
    /// </summary>
    private static async Task<string?> ReadVersionAsync(string binaryPath, CancellationToken cancel)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = new Process();
            proc.StartInfo = psi;
            if (!proc.Start())
                return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancel);
            await proc.WaitForExitAsync(cancel);
            var stdout = await stdoutTask;

            // llama.cpp prints e.g. "llama-server (llama) b9553 (...)
            // version header (build: 9553)". The first non-empty line is the tag line.
            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(
                line => line.Trim()).FirstOrDefault(trimmed => trimmed.Length > 0
            );
        }
        catch
        {
            return null;
        }
    }
}
