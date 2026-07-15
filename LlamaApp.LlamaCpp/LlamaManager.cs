using System.Diagnostics;
using System.Linq;
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
/// the app manages the install-script path (<c>%USERPROFILE%\.llama-app\llama.exe</c>,
/// what <c>install.ps1</c> produces) and may install/update it; any other install
/// (e.g. a manually built binary on PATH) is treated as unmanaged and left alone.
/// The installation is silent (writes under the user profile, no elevation needed).</para>
///
/// <para>Call <see cref="EnsureLlamaOrDownloadAsync"/> at startup; it adopts a
/// running server, launches one, or downloads the binary on demand, and reports
/// progress/state via <see cref="StateChanged"/>. Once the server is reachable,
/// <see cref="GetModelsAsync"/> lists locally available models via the
/// <c>GET /models</c> REST endpoint.</para>
/// </summary>
public sealed class LlamaManager
{
    /// <summary>Shared singleton, matching the macOS app's <c>.shared</c>.</summary>
    public static LlamaManager Shared { get; } = new();

    /// <summary>URL of the official Windows install script.</summary>
    private static readonly Uri InstallScriptUrl = new("https://llama.app/install.ps1");

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// The install-script path the app manages (matches <c>install.ps1</c>'s
    /// layout). The real binary lives in <c>%USERPROFILE%\.llama-app</c>;
    /// <c>install.ps1</c> also adds it to PATH.
    /// </summary>
    private static string ManagedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llama-app");

    /// <summary>Path to the app-managed <c>llama.exe</c>.</summary>
    private static string ManagedBinaryPath => Path.Combine(ManagedDir, "llama.exe");

    /// <summary>
    /// Where the resolved binary comes from — surfaced in the flyout footer, so
    /// an external installation isn't mistaken for the app's own (and a stale
    /// version isn't mistaken for a bug).
    /// </summary>
    public enum Origin
    {
        /// <summary>Not yet resolved — no binary found and no install attempted.</summary>
        Unknown,
        /// <summary>App-managed binary at <see cref="ManagedBinaryPath"/>.</summary>
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

    /// <summary>Server lifecycle state, surfaced in the UI.</summary>
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

    /// <summary>Port the local llama server listens on (matches the flyout link).</summary>
    public const int ServerPort = 2276;

    /// <summary>
    /// Hugging Face cache directory passed to the server via
    /// <c>HF_HUB_CACHE</c> so it resolves downloaded models from the same
    /// location the app scans. Set by the caller (App.OnLaunched reads it from
    /// <c>Settings.Current.CacheDirectory</c>) — kept here rather than reading
    /// <c>Settings</c> directly to avoid a circular project dependency.
    /// </summary>
    public string? CacheDirectory { get; set; }

    private InstallState _state = InstallState.Idle;
    private string? _failureMessage;
    private string? _binaryPath;
    private string? _version;
    private Origin _origin = Origin.Unknown;
    private ServerState _serverState = ServerState.Stopped;
    private Process? _serverProcess;

    /// <summary>Current installation state.</summary>
    public InstallState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>User-facing reason for the <see cref="Failed"/> state, if any.</summary>
    public string? FailureMessage
    {
        get => _failureMessage;
        private set { _failureMessage = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Path to the resolved <c>llama.exe</c>, or <c>null</c> if none.</summary>
    public string? BinaryPath
    {
        get => _binaryPath;
        private set { _binaryPath = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Version string reported by the resolved binary, or <c>null</c>.</summary>
    public string? Version
    {
        get => _version;
        private set
        {
            _version = value;
            // Keep LlamaRunner.Version in sync so the footer reads live.
            LlamaRunner.VersionCache = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Where the resolved binary comes from.</summary>
    public Origin CurrentOrigin
    {
        get => _origin;
        private set { _origin = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Current server state.</summary>
    public ServerState ServerStatus
    {
        get => _serverState;
        private set
        {
            if (_serverState == value) return;
            _serverState = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised whenever any observable property changes.</summary>
    public event EventHandler? StateChanged;

    private LlamaManager() { }

    /// <summary>
    /// Ensures a llama server is reachable at <c>localhost:<see cref="ServerPort"/></c> —
    /// the app's single point of contact for the model REST API. Resolution order:
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
        // 1. Adopt an already-running server (no binary/process needed).
        if (await IsServerReachableAsync(cancel))
        {
            ServerStatus = ServerState.Running;
            // Best-effort: resolve the binary so Version is populated for display,
            // but don't block the client on it.
            _ = ResolveAndReadVersionAsync(cancel);
            return true;
        }

        // 2/3. Resolve the binary; install if missing; then launch the server.
        var resolved = Resolve();
        switch (resolved.Kind)
        {
            case ResolutionKind.Managed:
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
                var installed = await InstallAsync(cancel);
                if (installed)
                    return await StartServerAsync(cancel);
                return false;
        }
    }

    /// <summary>
    /// Probes <c>GET /health</c> on the server port. Any HTTP response means a
    /// server is already up and listening (a connection-refused means not).
    /// </summary>
    private static async Task<bool> IsServerReachableAsync(CancellationToken cancel)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await client.GetAsync($"http://localhost:{ServerPort}/health", cancel);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort binary resolution + version read for an adopted (external)
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
        catch { /* best-effort */ }
    }

    /// <summary>
    /// (Re)installs the app-managed binary by downloading and executing
    /// <see cref="InstallScriptUrl"/>. Also, the retry entry point.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken cancel = default)
    {
        State = InstallState.Installing;
        try
        {
            await DownloadAndRunInstallerAsync(cancel);
            // install.ps1 writes llama.exe to the managed dir; confirm it landed.
            if (!File.Exists(ManagedBinaryPath))
                throw new IOException($"Install script finished but {ManagedBinaryPath} was not created.");

            BinaryPath = ManagedBinaryPath;
            CurrentOrigin = Origin.Managed;
            Version = await ReadVersionAsync(ManagedBinaryPath, cancel);
            State = InstallState.Idle;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FailureMessage = ex.Message;
            State = InstallState.Failed;
            return false;
        }
    }

    // ---- Server ----

    /// <summary>
    /// Launches <c>llama serve --port 2276</c> as a background process and polls
    /// the port until it responds (or times out). Called automatically by
    /// <see cref="EnsureLlamaOrDownloadAsync"/> once a binary is available. No-op (returns
    /// true) if the server is already running.
    /// </summary>
    public async Task<bool> StartServerAsync(CancellationToken cancel = default)
    {
        if (ServerStatus == ServerState.Running) return true;
        if (BinaryPath is null || !File.Exists(BinaryPath))
        {
            ServerStatus = ServerState.Failed;
            return false;
        }

        StopServer(); // reclaim any prior instance / port

        ServerStatus = ServerState.Starting;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BinaryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // `serve` is the unified subcommand (replaces the old llama-server).
            // Router mode hosts the webui and serves requests even with no model
            // loaded — models load on demand. --jinja enables chat templates.
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(ServerPort.ToString());
            psi.ArgumentList.Add("--jinja");

            // Point the HF cache at the user-configured directory so the server
            // resolves downloaded models from the same place the app scans.
            if (!string.IsNullOrEmpty(CacheDirectory) && Directory.Exists(CacheDirectory))
                psi.EnvironmentVariables["HF_HUB_CACHE"] = CacheDirectory;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) =>
            {
                // Fires on a thread-pool thread; marshal to UI thread via the
                // continuation below is unnecessary since ServerStatus is a
                // simple set — but keep it thread-safe by not touching the
                // process field here. Only flip state if this wasn't an
                // intentional stop (StopServer sets Stopped before killing).
                if (ServerStatus != ServerState.Stopped)
                    ServerStatus = ServerState.Failed;
            };

            if (!proc.Start())
            {
                ServerStatus = ServerState.Failed;
                return false;
            }
            _serverProcess = proc;

            // Wait for the port to respond — the server takes a moment to bind.
            if (await WaitForPortAsync(TimeSpan.FromSeconds(15), cancel))
            {
                ServerStatus = ServerState.Running;
                return true;
            }

            // Timed out waiting for the port — the process may have exited.
            ServerStatus = ServerState.Failed;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ServerStatus = ServerState.Failed;
            return false;
        }
    }

    /// <summary>
    /// Stops the running server process (if any). Safe to call repeatedly.
    /// Sets state to Stopped before killing so the Exited handler doesn't flip
    /// to Failed.
    /// </summary>
    public void StopServer()
    {
        ServerStatus = ServerState.Stopped;
        var proc = _serverProcess;
        _serverProcess = null;
        if (proc == null || proc.HasExited) return;
        
        try { proc.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Polls <c>http://localhost:2276/health</c> until it responds or the timeout
    /// elapses. The llama server exposes a health endpoint once it's bound and
    /// ready; this confirms the port is actually serving rather than just
    /// waiting a fixed delay.
    /// </summary>
    private static async Task<bool> WaitForPortAsync(TimeSpan timeout, CancellationToken cancel)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        var url = $"http://localhost:{ServerPort}/health";

        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                // Any HTTP response (even an error code) means the server is
                // up and listening — a connection-refused means it's not yet.
                var resp = await client.GetAsync(url, cancel);
                return true;
            }
            catch
            {
                await Task.Delay(250, cancel);
            }
        }
        return false;
    }

    // ---- Model download ----

    /// <summary>
    /// Downloads a model by asking the running llama server to fetch it. The
    /// server (router mode) handles the actual Hugging Face transfer; this
    /// method just <b>POST</b>s <c>{"model": "&lt;name&gt;"}</c> to
    /// <c>/models</c> and tracks progress via the <c>/models/sse</c> stream.
    /// </summary>
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
    /// <param name="model">The model to download; <see cref="IModel.Name"/> is
    /// the Hugging Face repo id (e.g. <c>ggml-org/gpt-oss-20b-GGUF</c>).</param>
    /// <param name="progress">Receives <see cref="ModelDownloadProgress"/> updates
    /// as the server streams them. May be <c>null</c>.</param>
    /// <param name="cancel">Cancels the download (closes the SSE stream and
    /// asks the server to stop via <c>DELETE /models/{name}</c>).</param>
    /// <returns><c>true</c> if the download finished successfully;
    /// <c>false</c> on failure or cancellation.</returns>
    public async Task<bool> DownloadModelAsync(IModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
        {
            progress?.Report(new Common.ModelDownloadProgress(
                model.Name, 0, 0, Done: false, Failed: true, Message: "Server is not running"));
            return false;
        }

        var baseUrl = $"http://localhost:{ServerPort}";
        var modelName = model.Name;

        using var sseClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var postClient = Client;

        // Open the SSE stream first so we don't miss the earliest progress events.
        // HttpCompletionOption.ResponseHeadersRead lets us read the body as it
        // arrives rather than buffering the whole (infinite) stream.
        var sseResponse = await sseClient.GetAsync(
            $"{baseUrl}/models/sse",
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
        var payload = $$"""{"model":"{{modelName}}"}""";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var postResp = await postClient.PostAsync($"{baseUrl}/models", content, cancel);
            if (!postResp.IsSuccessStatusCode)
            {
                var body = await postResp.Content.ReadAsStringAsync(cancel);
                await sseCts.CancelAsync();
                progress?.Report(new Common.ModelDownloadProgress(
                    modelName, 0, 0, Done: false, Failed: true,
                    Message: $"Server rejected the request ({(int)postResp.StatusCode}): {body}"));
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sseCts.CancelAsync();
            progress?.Report(new Common.ModelDownloadProgress(
                modelName, 0, 0, Done: false, Failed: true, Message: ex.Message));
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
        // cancellation and skipping LaunchModelAsync. Breaking lets the finally
        // block cancel + dispose the stream cleanly with no thrown exception.
        var success = false;
        var completed = false;
        try
        {
            await foreach (var (evt, modelId, data) in ParseSseStreamAsync(reader, sseCts.Token))
            {
                cancel.ThrowIfCancellationRequested();
                if (!string.Equals(modelId, modelName, StringComparison.OrdinalIgnoreCase) && modelId != "*")
                    continue; // another model's event

                switch (evt)
                {
                    case "download_progress":
                        var (downloaded, total) = SumProgress(data);
                        progress?.Report(new Common.ModelDownloadProgress(
                            modelName, downloaded, total, Done: false, Failed: false));
                        break;

                    case "download_finished":
                        success = true;
                        progress?.Report(new Common.ModelDownloadProgress(
                            modelName, 0, 0, Done: true, Failed: false, Message: "Download complete"));
                        completed = true;
                        break;

                    case "download_failed":
                        progress?.Report(new Common.ModelDownloadProgress(
                            modelName, 0, 0, Done: false, Failed: true, Message: "Download failed"));
                        completed = true;
                        break;
                }

                if (completed) break; // exit the await foreach; finally cleans up
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // User canceled — ask the server to stop the download.
            await CancelServerDownloadAsync(postClient, baseUrl, modelName);
            progress?.Report(new Common.ModelDownloadProgress(
                modelName, 0, 0, Done: false, Failed: false, Message: "Cancelled"));
            throw;
        }
        finally
        {
            await sseCts.CancelAsync();
            try { sseResponse.Dispose(); } catch { /* best-effort */ }
        }

        return success;
    }

    /// <summary>
    /// Asks the running llama server to load (launch) a model into memory via
    /// <c>POST /models/load</c>. In router mode, the server spawns a child
    /// process for the model; this returns once the load request is accepted.
    /// </summary>
    /// <param name="model">The model to load; <see cref="Common.IModel.Name"/> is the
    /// repo id the server knows (must be downloaded first).</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns><c>true</c> if the server accepted the load request.</returns>
    public async Task<bool> LaunchModelAsync(IModel model, CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
            return false;

        var baseUrl = $"http://localhost:{ServerPort}";

        try
        {
            const string payload = $$$"""{"model":"{{model.ServerModelId}}"}""";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Client.PostAsync($"{baseUrl}/models/load", content, cancel);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ---- Model listing (GET /models) ----

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
        /// <summary>Load state: <c>loaded</c> or <c>unloaded</c>.</summary>
        public string Status { get; init; } = "";
        /// <summary>True when <c>architecture.input_modalities</c> contains <c>image</c>.</summary>
        public bool SupportsImage { get; init; }
        /// <summary>All declared input modalities (e.g. <c>text</c>, <c>image</c>).</summary>
        public IReadOnlyList<string> InputModalities { get; init; } = Array.Empty<string>();
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

        var baseUrl = $"http://localhost:{ServerPort}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var resp = await client.GetAsync($"{baseUrl}/models", cancel);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
            var dto = await JsonSerializer.DeserializeAsync<ModelsResponseDto>(stream, cancellationToken: cancel);
            return dto?.Data?.Select(Map).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static ServerModel Map(ServerModelDto d) => new()
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

    // ---- /models JSON DTOs ----

    private sealed class ModelsResponseDto
    {
        [JsonPropertyName("data")] public List<ServerModelDto>? Data { get; init; }
    }

    private sealed class ServerModelDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("status")] public ModelStatusDto? Status { get; set; }
        [JsonPropertyName("architecture")] public ArchitectureDto? Architecture { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("can_remove")] public bool CanRemove { get; set; }
    }

    private sealed class ModelStatusDto
    {
        [JsonPropertyName("value")] public string Value { get; set; } = "";
    }

    private sealed class ArchitectureDto
    {
        [JsonPropertyName("input_modalities")] public List<string>? InputModalities { get; set; }
        [JsonPropertyName("output_modalities")] public List<string>? OutputModalities { get; set; }
    }

    /// <summary>
    /// Parses an SSE stream line-by-line, yielding (<c>event</c>, <c>model</c>,
    /// <c>data</c> JSON) tuples. Standard SSE framing: <c>data:</c> lines carry
    /// the payload, a blank line dispatches the event.
    /// </summary>
    private static async IAsyncEnumerable<(string Event, string Model, JsonElement Data)> ParseSseStreamAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        var pendingData = new System.Text.StringBuilder();

        while (!cancel.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancel);
            if (line is null) yield break; // stream closed

            if (line.Length == 0)
            {
                // Blank line = dispatch the accumulated event.
                if (pendingData.Length > 0)
                {
                    var json = pendingData.ToString();
                    pendingData.Clear();

                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var evt = root.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
                    var mdl = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    var data = root.TryGetProperty("data", out var d) ? d : default;

                    if (evt.Length > 0)
                        yield return (evt, mdl, data);
                }
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
    }

    /// <summary>
    /// Sums <c>done</c>/<c>total</c> bytes across all URLs in a
    /// <c>download_progress</c> data payload (a repo can have multiple files).
    /// </summary>
    private static (long downloaded, long total) SumProgress(JsonElement data)
    {
        long downloaded = 0, total = 0;
        if (data.ValueKind != JsonValueKind.Object) return (0, 0);

        if (!data.TryGetProperty("progress", out var progress) ||
            progress.ValueKind != JsonValueKind.Object) return (downloaded, total);
        
        foreach (var url in progress.EnumerateObject().Where(url => url.Value.ValueKind == JsonValueKind.Object))
        {
            if (url.Value.TryGetProperty("done", out var done))
                downloaded += done.TryGetInt64(out var d) ? d : 0;
            if (url.Value.TryGetProperty("total", out var tot))
                total += tot.TryGetInt64(out var t) ? t : 0;
        }

        return (downloaded, total);
    }

    /// <summary>
    /// Asks the server to cancel an in-flight download via
    /// <c>DELETE /models/{name}</c>. Best-effort — the server may have already
    /// finished or the request may fail; either way the SSE stream is closed
    /// by the caller's cancellation.
    /// </summary>
    private static async Task CancelServerDownloadAsync(HttpClient client, string baseUrl, string modelName)
    {
        try
        {
            using var resp = await client.DeleteAsync($"{baseUrl}/models/{Uri.EscapeDataString(modelName)}");
        }
        catch
        {
            // Best-effort — don't surface cancel cleanup failures.
        }
    }

    // ---- Resolution ----

    private enum ResolutionKind { Missing, Managed, External }

    private record Resolution(ResolutionKind Kind, string? Path);

    /// <summary>
    /// Resolves the active <c>llama</c> binary. The managed path wins, then PATH,
    /// else <see cref="ResolutionKind.Missing"/>.
    /// </summary>
    private static Resolution Resolve()
    {
        if (File.Exists(ManagedBinaryPath))
            return new Resolution(ResolutionKind.Managed, ManagedBinaryPath);

        // Probe PATH for an existing (unmanaged) install.
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return new Resolution(ResolutionKind.Missing, null);
        
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "llama.exe");
                if (File.Exists(candidate))
                    return new Resolution(ResolutionKind.External, candidate);
            }
            catch
            {
                // Malformed PATH entry — skip.
            }
        }

        return new Resolution(ResolutionKind.Missing, null);
    }

    // ---- Install ----

    /// <summary>
    /// Downloads <see cref="InstallScriptUrl"/> to a temp file and runs it with
    /// PowerShell (<c>-ExecutionPolicy Bypass -File</c>), inheriting the app's
    /// stdout/stderr for logging. Throws on a non-zero exit code or download
    /// failure. Mirrors what running <c>iex (iwr llama.app/install.ps1)</c> does
    /// but as an explicit downloaded file so the script source is auditable.
    /// </summary>
    private static async Task DownloadAndRunInstallerAsync(CancellationToken cancel)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"llama-install-{Guid.NewGuid():N}.ps1");
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaApp/1.0");
                using var resp = await client.GetAsync(InstallScriptUrl, cancel);
                
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(scriptPath);
                await resp.Content.CopyToAsync(fs, cancel);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
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

            using var proc = new Process();
            proc.StartInfo = psi;
            if (!proc.Start())
                throw new InvalidOperationException("Could not start the install script.");

            // Stream output to debug traces for diagnostics; not surfaced to the UI.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancel);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancel);
            await proc.WaitForExitAsync(cancel);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                throw new IOException($"install.ps1 exited with code {proc.ExitCode}.\n{stderr}");
            }
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