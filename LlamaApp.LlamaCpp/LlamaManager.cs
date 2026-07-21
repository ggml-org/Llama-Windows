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

    private Process? _serverProcess;

    // Single-flight guard for EnsureLlamaOrDownloadAsync / StartServerAsync. Called
    // fire-and-forget from App.OnLaunched and re-entrant via StateChanged
    // handlers; without it, two concurrent callers can both pass the initial
    // "no server reachable" probe and both spawn a `llama serve --port 2276`,
    // leaking processes (one fails to bind and may linger; second binds and eats
    // RAM). The gate serializes launches within one process; cross-instance
    // races are handled by the retrying adoption probe (see WaitForReachableAsync).
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    // Model-state poller: a background loop that fetches /models every second
    // while the server is Running and publishes the snapshot via ModelsChanged.
    private CancellationTokenSource? _pollerCts;

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

    /// <summary>Current server state.</summary>
    public ServerState ServerStatus
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            // The model-state poller runs only while the server is up: start it
            // on Running, tear it down on any other state (Stopped/Failed).
            if (value == ServerState.Running) StartModelPoller();
            else StopModelPoller();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    } = ServerState.Stopped;

    /// <summary>Raised whenever any observable property changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Rose on a background thread roughly once per second with a fresh
    /// <c>GET /models</c> snapshot while the server is <see cref="ServerState.Running"/>.
    /// Handlers should marshal to the UI thread before touching view models.
    /// </summary>
    public event EventHandler<IReadOnlyList<ServerModel>>? ModelsChanged;

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
        // answer the very first probe, and a single miss used to spawn a
        // SECOND `llama serve --port 2276` here — leaving two processes eating
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
        finally { _ensureGate.Release(); }
    }

    /// <summary>
    /// Probes <c>GET /health</c> on the server port once. Any HTTP response
    /// means a server is already listening (connection-refused means not). The
    /// atomic unit used by the retrying <see cref="WaitForReachableAsync"/> and
    /// by the last-chance re-probe in <see cref="StartServerAsync"/>.
    /// </summary>
    private static async Task<bool> ProbeHealthAsync(CancellationToken cancel)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var resp = await client.GetAsync($"http://localhost:{ServerPort}/health", cancel);
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
    /// fix for several <c>llama serve --port 2276</c> processes piling up and
    /// eating RAM. The window is short so a genuinely-absent server doesn't
    /// delay startup by much (each refusal is near-instant; the 250ms cadence
    /// is what bounds the worst case).
    /// </summary>
    private static async Task<bool> WaitForReachableAsync(TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            if (await ProbeHealthAsync(cancel)) return true;
            try { await Task.Delay(250, cancel); }
            catch (OperationCanceledException) { throw; }
        }
        return false;
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
        catch (Exception ex) { Log.Warn(ex, "best-effort version resolve failed"); }
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
            Log.Error(ex, "llama binary install failed");
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

        // Last-chance adoption: between EnsureLlamaOrDownloadAsync's probe and
        // now (esp. after a slow install.ps1 download), a sibling instance or a
        // manual launch may have brought up a server on our port. Adopting it
        // here avoids spawning a duplicate that would fail to bind and orphan
        // — the exact leak that left several servers eating RAM.
        if (await ProbeHealthAsync(cancel))
        {
            Log.Info("adopted an already-running llama server (pre-start re-probe)");
            ServerStatus = ServerState.Running;
            _ = ResolveAndReadVersionAsync(cancel);
            return true;
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

            Log.Info($"starting llama server: {BinaryPath} serve --port {ServerPort} --jinja");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) =>
            {
                // Fires on a thread-pool thread; marshal to UI thread via the
                // continuation below is unnecessary since ServerStatus is a
                // simple set — but keep it thread-safe by not touching the
                // process field here. Only flip state if this wasn't an
                // intentional stop (StopServer sets Stopped before killing).
                if (ServerStatus != ServerState.Stopped)
                {
                    Log.Warn($"llama server process exited unexpectedly (code={proc.ExitCode})");
                    ServerStatus = ServerState.Failed;
                }
                else
                {
                    Log.Info("llama server process exited (intentional stop)");
                }
            };

            if (!proc.Start())
            {
                Log.Error("llama server process failed to start (proc.Start returned false)");
                ServerStatus = ServerState.Failed;
                return false;
            }
            _serverProcess = proc;

            // Wait for the port to respond — the server takes a moment to bind.
            // We pass `proc` so the wait fast-fails if the process exits before
            // becoming ready (e.g. it couldn't bind the port because a sibling
            // already did) instead of polling for the full 15s timeout.
            if (await WaitForPortAsync(proc, TimeSpan.FromSeconds(15), cancel))
            {
                Log.Info("llama server is reachable");
                ServerStatus = ServerState.Running;
                return true;
            }

            // Timed out (or the process exited early). DON'T leave the spawned
            // process running: a prior timeout-then-abandon left the server
            // alive, and a later app start (or this same retry) spawned a
            // second on the same port → two servers eating RAM. Kill ours so
            // the port is free for the next attempt. StopServer sets Stopped
            // before killing so the Exited handler logs an intentional stop,
            // then we surface the failure.
            Log.Error("llama server failed to become ready within 15s (port probe timed out)");
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
        catch (Exception ex) { Log.Warn(ex, "best-effort server kill failed"); }
    }

    /// <summary>
    /// Polls <c>http://localhost:2276/health</c> until it responds or the timeout
    /// elapses (or the spawned <paramref name="proc"/> exits first). The llama
    /// server exposes a health endpoint once it's bound and ready; this confirms
    /// the port is actually serving rather than just waiting a fixed delay.
    /// Checking <c>proc.HasExited</c> each iteration fast-fails when the process
    /// died right after launch (e.g. it couldn't bind the port because a
    /// sibling already did) so we don't sit out the full timeout before tearing
    /// down — and we don't keep around a dead-but-tracked process reference.
    /// </summary>
    private static async Task<bool> WaitForPortAsync(Process proc, TimeSpan timeout, CancellationToken cancel)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        var url = $"http://localhost:{ServerPort}/health";

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

    // ---- Model-state poller ----

    /// <summary>
    /// Starts the background <c>GET /models</c> poller (every 1s) that publishes
    /// fresh snapshots via <see cref="ModelsChanged"/>. Idempotent — restarts the
    /// loop if one is already running. Torn down automatically when the server
    /// leaves <see cref="ServerState.Running"/> (see <see cref="ServerStatus"/> setter).
    /// </summary>
    private void StartModelPoller()
    {
        StopModelPoller();
        _pollerCts = new CancellationTokenSource();
        var token = _pollerCts.Token;
        Task.Run(() => PollModelsAsync(token), token);
        Log.Info("model-state poller started (1s interval)");
    }

    /// <summary>Stops the poller and releases its cancellation token. Safe to call repeatedly.</summary>
    private void StopModelPoller()
    {
        try { _pollerCts?.Cancel(); } catch { /* best-effort */ }
        try { _pollerCts?.Dispose(); } catch { /* best-effort */ }
        _pollerCts = null;
        
        // Don't await the loop: it exits on cancel within one delay interval;
        // awaiting would block the UI thread (the ServerStatus setter runs on it).
    }

    /// <summary>
    /// The poll loop: fetch <c>/models</c> every second and raise
    /// <see cref="ModelsChanged"/> with the snapshot. Transient errors are
    /// swallowed — the UI reconcile is additive and never clears rows on an
    /// empty/error fetch, so a network blip doesn't flicker the list. Exits
    /// cleanly on cancellation.
    /// </summary>
    private async Task PollModelsAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            IReadOnlyList<ServerModel> snapshot = [];
            try { snapshot = await GetModelsAsync(cancel); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Debug($"model poll fetch failed: {ex.Message}"); /* transient — keep the previous snapshot in effect */ }

            try { _lastModelsSnapshot = snapshot; ModelsChanged?.Invoke(this, snapshot); }
            catch (Exception ex) { Log.Warn(ex, "ModelsChanged handler threw"); /* a handler error doesn't take down the poller */ }

            if (snapshot.Count > 0)
                Log.Debug(
                    $"poll: {snapshot.Count} model(s): {string.Join(", ", snapshot.Select(m => $"{m.Id}={(m.Status ?? "?")}"))}");

            try { await Task.Delay(1000, cancel); }
            catch (OperationCanceledException) { break; }
        }
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
        Log.Info($"downloading model {model.Name}");
        if (ServerStatus != ServerState.Running)
        {
            progress?.Report(new ModelDownloadProgress(
                model.Name, 0, 0, Done: false, Failed: true, Message: "Server is not running"));
            return false;
        }

        var baseUrl = $"http://localhost:{ServerPort}";
        var modelName = model.Name;

        using var sseClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

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
            using var postResp = await Client.PostAsync($"{baseUrl}/models", content, cancel);
            if (!postResp.IsSuccessStatusCode)
            {
                var body = await postResp.Content.ReadAsStringAsync(cancel);
                await sseCts.CancelAsync();
                progress?.Report(new ModelDownloadProgress(
                    modelName, 0, 0, Done: false, Failed: true,
                    Message: $"Server rejected the request ({(int)postResp.StatusCode}): {body}"));
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "download POST threw");
            await sseCts.CancelAsync();
            progress?.Report(new ModelDownloadProgress(
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
        // cancellation and skipping the post-download load. Breaking lets the final
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
                        Log.Warn($"server reported download_failed for {modelName}");
                        progress?.Report(new Common.ModelDownloadProgress(
                            modelName, 0, 0, Done: false, Failed: true, Message: "Download failed"));
                        completed = true;
                        break;
                }

                if (completed) break; // exit to await foreach; finally cleans up
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // User canceled — ask the server to stop the download.
            await CancelServerDownloadAsync(Client, baseUrl, modelName);
            progress?.Report(new ModelDownloadProgress(
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
    /// process for the model; this returns once the load request is accepted —
    /// the model isn't necessarily <c>loaded</c> yet. Track the transition via
    /// the <see cref="ModelsChanged"/> poller, which reports the server's
    /// <c>status</c> field flipping from <c>unloaded</c> to <c>loaded</c>.
    /// </summary>
    /// <param name="model">The model to load; <see cref="Common.IModel.ServerModelId"/>
    /// is the canonical id the server knows (the HF repo id with its
    /// <c>:&lt;quant&gt;</c> suffix, e.g. <c>ggml-org/gemma-3-4b-it-GGUF:Q4_K_M</c>) —
    /// <c>/models/load</c> requires the quant suffix, so the bare repo id won't do.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns><c>true</c> if the server accepted the load request.</returns>
    public async Task<bool> LoadModelAsync(IModel model, CancellationToken cancel = default)
    {
        if (ServerStatus != ServerState.Running)
            return false;

        var baseUrl = $"http://localhost:{ServerPort}";

        try
        {
            Log.Info($"loading model {model.ServerModelId}");
            
            var payload = $$"""{"model":"{{model.ServerModelId}}"}""";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Client.PostAsync($"{baseUrl}/models/load", content, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"server rejected model load ({(int)resp.StatusCode})");
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "model load request threw");
            return false;
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

        var baseUrl = $"http://localhost:{ServerPort}";

        try
        {
            Log.Info($"unloading model {model.ServerModelId}");
            
            var payload = $$"""{"model":"{{model.ServerModelId}}"}""";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Client.PostAsync($"{baseUrl}/models/unload", content, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"server rejected model unload ({(int)resp.StatusCode})");
            }
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
        /// <summary>Load state reported by the server: <c>unloaded</c>, <c>loading</c>, or <c>loaded</c>.</summary>
        public string Status { get; init; } = "";
        /// <summary>True when <see cref="Status"/> is <c>loaded</c> (model resident in a child process).</summary>
        public bool IsLoaded => string.Equals(Status, "loaded", StringComparison.OrdinalIgnoreCase);
        /// <summary>True when <see cref="Status"/> is <c>loading</c> (load in progress: child process spawning / weights mmapping).</summary>
        public bool IsLoading => string.Equals(Status, "loading", StringComparison.OrdinalIgnoreCase);
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
    /// snapshot so a hotkey press doesn't block on <c>GET /models</c>.
    /// </summary>
    private IReadOnlyList<ServerModel> _lastModelsSnapshot = [];

    /// <summary>Latest known loaded model id, or <c>null</c> when none is loaded.</summary>
    public string? LoadedModelId => (from m in _lastModelsSnapshot where m.IsLoaded select m.Id).FirstOrDefault();

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

        var baseUrl = $"http://localhost:{ServerPort}";
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var body = $$"""{"model":"{{model}}","stream":true, "return_progress": true, "messages":[{"role":"user","content":{{JsonString(userMessage)}}}]}""";
        // SendAsync with ResponseHeadersRead returns as soon as the response
        // headers arrive, so we can read the SSE body incrementally below.
        // PostAsync (the default ResponseContentRead) would buffer the entire
        // response before completing — defeating streaming and making the
        // overlay hang until the whole generation finished.
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"{baseUrl}/v1/chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Log.Info($"chat completion → POST /v1/chat/completions (model={model}, prompt={userMessage.Length} chars)");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
        Log.Info($"chat completion ← HTTP {(int)resp.StatusCode} {resp.StatusCode}");
        resp.EnsureSuccessStatusCode();

        // Reuse the same SSE line framing as /models/sse: data: {json} lines,
        // terminated by a blank line / "data: [DONE]". We parse incrementally so
        // tokens surface as soon as the server flushes them.
        // Default buffer size matches the working /models/sse path.
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
                var preview = line.Length > 200 ? line.Substring(0, 200) + "…" : line;
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
            if (!string.IsNullOrEmpty(text))
            {
                yielded++;
                yield return text;
            }
        }
        // ReadLineAsync returned null: the server closed the stream without
        // sending [DONE]. Log so we can tell a hang (no log) from a clean
        // close with zero parsed chunks (this line).
        Log.Info($"chat completion stream ended without [DONE]: {yielded} chunk(s) yielded");
    }

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
                case '\\': sb.Append("\\\\"); break;
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

            Log.Info($"running install.ps1 from {InstallScriptUrl}");
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