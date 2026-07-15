using System.Diagnostics;
using System.IO;

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
/// <para>Call <see cref="EnsureReadyAsync"/> at startup; it installs on demand and
/// reports progress/state via <see cref="StateChanged"/>. <see cref="Version"/>
/// (and <see cref="LlamaRunner.Version"/>) is populated once a binary is found.</para>
/// </summary>
public sealed class LlamaManager
{
    /// <summary>Shared singleton, matching the macOS app's <c>.shared</c>.</summary>
    public static LlamaManager Shared { get; } = new();

    /// <summary>URL of the official Windows install script.</summary>
    private static readonly Uri InstallScriptUrl = new("https://llama.app/install.ps1");

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
    /// Where the resolved binary comes from — surfaced in the flyout footer so
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
        /// <summary>The installation failed; retry via <see cref="EnsureReadyAsync"/>.</summary>
        Failed,
    }

    private InstallState _state = InstallState.Idle;
    private string? _failureMessage;
    private string? _binaryPath;
    private string? _version;
    private Origin _origin = Origin.Unknown;

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

    /// <summary>Raised whenever any observable property changes.</summary>
    public event EventHandler? StateChanged;

    private LlamaManager() { }

    /// <summary>
    /// Resolves the local <c>llama</c> binary and installs it if missing.
    /// Returns <c>true</c> when a usable binary is available afterward (always,
    /// except a failed install). Safe to await from the UI thread; the install
    /// runs on a background process and <see cref="StateChanged"/> fires on the
    /// UI thread via the awaited continuation.
    /// </summary>
    /// <param name="cancel">Optional cancellation token.</param>
    public async Task<bool> EnsureReadyAsync(CancellationToken cancel = default)
    {
        var resolved = Resolve();
        switch (resolved.Kind)
        {
            case ResolutionKind.Managed:
                BinaryPath = resolved.Path;
                CurrentOrigin = Origin.Managed;
                Version = await ReadVersionAsync(resolved.Path, cancel);
                State = InstallState.Idle;
                return true;

            case ResolutionKind.External:
                BinaryPath = resolved.Path;
                CurrentOrigin = Origin.External;
                Version = await ReadVersionAsync(resolved.Path, cancel);
                State = InstallState.Idle;
                return true;

            default: // Missing
                return await InstallAsync(cancel);
        }
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
            // version header (build: 9553)". First non-empty line is the tag line.
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