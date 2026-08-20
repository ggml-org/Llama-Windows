using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// One row of a <c>llama fit-params --fit-print on</c> estimate: the memory
/// (in bytes, converted from the CLI's MiB) that a device — or the host —
/// needs for the model weights, the context (KV cache), and the compute
/// buffers at the requested context length.
/// </summary>
public sealed record DeviceMemoryUsage
{
    /// <summary>Row name as printed: <c>CUDA0</c>, <c>Vulkan1</c>, <c>Host</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Model weights on this device/host, in bytes.</summary>
    public ulong ModelBytes { get; init; }

    /// <summary>KV cache at the requested context length, in bytes.</summary>
    public ulong ContextBytes { get; init; }

    /// <summary>Compute buffers, in bytes.</summary>
    public ulong ComputeBytes { get; init; }

    /// <summary>Total requirement of this row.</summary>
    public ulong TotalBytes => ModelBytes + ContextBytes + ComputeBytes;
}

/// <summary>
/// The full memory estimate for a model at a context length, as reported by
/// <c>llama fit-params</c> — one row per accelerator device plus the host
/// row. Unlike <see cref="ContextMemoryEstimate"/> (a header-derived
/// weights + KV heuristic), this is llama.cpp's own accounting for the exact
/// GGUF: real KV dtype, compute buffers, and the host-resident tensors that
/// remain even with every layer offloaded.
/// </summary>
public sealed record FitParamsEstimate
{
    /// <summary>Accelerator device rows (empty on a CPU-only probe).</summary>
    public IReadOnlyList<DeviceMemoryUsage> Devices { get; init; } = [];

    /// <summary>The <c>Host</c> row — always present.</summary>
    public required DeviceMemoryUsage Host { get; init; }

    /// <summary>Total requirement across every row.</summary>
    public ulong TotalBytes =>
        Devices.Aggregate(Host.TotalBytes, (sum, d) => sum + d.TotalBytes);
}

/// <summary>
/// Probes whether a model fits at a given context length by running the CLI's
/// <c>fit-params</c> tool in its estimate-printing mode:
///
/// <code>
/// llama fit-params -m model.gguf -c 32768 --fit-print on
/// </code>
///
/// stdout carries one row per device plus the host row (MiB):
///
/// <code>
/// CUDA0 680 534 514
/// Host 306 0 133
/// </code>
///
/// The tool itself only reports requirements — what fits is decided by the
/// caller against the live budgets (free VRAM from <see cref="DeviceQuery"/>,
/// usable RAM from <see cref="SystemMemory"/>). A <c>null</c> result means
/// "no verdict" (old/missing binary, unreadable model, failed probe) — the
/// caller then keeps its heuristic estimate, matching the fail-open
/// convention everywhere else.
/// </summary>
public static partial class FitParamsQuery
{
    /// <summary>
    /// The log line announcing the estimate section — the anchor that tells
    /// the parser the following stdout rows are estimate data (the launcher
    /// exits 0 even for unknown commands, so the exit code is not a signal).
    /// </summary>
    internal const string Header = "printing estimated memory in MiB to stdout";

    private const ulong Mebibyte = 1024UL * 1024UL;

    /// <summary>
    /// Runs <c>fit-params --fit-print on</c> for <paramref name="modelPath"/>
    /// at <paramref name="contextTokens"/> and parses the estimate. Returns
    /// <c>null</c> when the tool is unavailable or its output is
    /// unrecognizable — never throws (a failed probe must not block the
    /// caller). Two argument forms are tried in order (bare
    /// <c>fit-params</c>, then the launcher's <c>cli fit-params</c> wrapper,
    /// mirroring <see cref="DeviceQuery"/>); a candidate is accepted only
    /// when its output contains the estimate section.
    /// </summary>
    public static async Task<FitParamsEstimate?> QueryAsync(
        string binaryPath, string modelPath, int contextTokens, CancellationToken cancel = default)
    {
        var tail = new[]
        {
            "-m", modelPath,
            "-c", contextTokens.ToString(CultureInfo.InvariantCulture),
            "--fit-print", "on",
        };

        foreach (var prefix in ArgumentPrefixes)
        {
            var args = prefix.Concat(tail).ToArray();
            string? output;
            try
            {
                output = await RunAsync(binaryPath, args, cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"fit-params probe failed ({string.Join(' ', prefix)}): {ex.Message}");
                continue;
            }

            if (output is not null && output.Contains(Header, StringComparison.Ordinal))
                return Parse(output);
        }

        return null;
    }

    /// <summary>
    /// Subcommand candidates, in priority order: the multi-tool binary (and
    /// the app-managed launcher, which accepts bare subcommands for
    /// <c>serve</c>) first, then the <c>cli</c>-wrapped form a wrapper-only
    /// launcher may need. A standalone CPU-era <c>llama-cli</c> knows neither
    /// — both candidates get rejected and the caller falls back to its
    /// heuristic.
    /// </summary>
    private static readonly string[][] ArgumentPrefixes =
    [
        ["fit-params"],
        ["cli", "fit-params"],
    ];

    /// <summary>
    /// Runs the binary with <paramref name="args"/> and returns its combined
    /// stdout + stderr (the estimate rows land on stdout, but the section
    /// anchor is a log line that can land on either stream depending on the
    /// build). Bounded by a 30s timeout — a big model's metadata read can
    /// take a while, but a hung probe is killed and reported as <c>null</c>.
    /// </summary>
    private static async Task<string?> RunAsync(string binaryPath, string[] args, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return null;

        // Read both streams concurrently — a full stderr buffer while we sit
        // on stdout (or vice versa) would deadlock the child.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancel);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancel);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return null;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return stdout + "\n" + stderr;
    }

    /// <summary>
    /// Parses a <c>--fit-print on</c> output (stdout + stderr combined) into
    /// an estimate. Rows are only considered after the
    /// <see cref="Header"/> anchor (backend init noise precedes it, and the
    /// log line may carry ANSI color codes); the section ends at the
    /// <c>Host</c> row, which the tool always prints last. <c>null</c> when
    /// no <c>Host</c> row parsed — a partial section is an unknown verdict,
    /// not a zero one. Internal for unit tests.
    /// </summary>
    internal static FitParamsEstimate? Parse(string output)
    {
        var devices = new List<DeviceMemoryUsage>();
        DeviceMemoryUsage? host = null;

        var inSection = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (!inSection)
            {
                if (line.Contains(Header, StringComparison.Ordinal))
                    inSection = true;
                continue;
            }

            var match = UsageLineRegex().Match(line.Trim());
            if (!match.Success)
                continue;

            if (!TryParseMib(match.Groups["model"].Value, out var model) ||
                !TryParseMib(match.Groups["context"].Value, out var context) ||
                !TryParseMib(match.Groups["compute"].Value, out var compute))
                continue;

            var usage = new DeviceMemoryUsage
            {
                Name = match.Groups["name"].Value,
                ModelBytes = model,
                ContextBytes = context,
                ComputeBytes = compute,
            };

            if (usage.Name == "Host")
            {
                host = usage;
                break; // Host is always the last row — the section is done.
            }
            devices.Add(usage);
        }

        return host is null
            ? null
            : new FitParamsEstimate { Devices = devices, Host = host };
    }

    /// <summary>
    /// One estimate row, e.g. <c>CUDA0 680 534 514</c> — a device/Host name
    /// followed by the model, context, and compute sizes in MiB. The row is
    /// whitespace-separated with a trailing space; log noise (timestamps,
    /// ANSI codes) never matches because of the anchored numeric tail.
    /// </summary>
    [GeneratedRegex(
        @"^(?<name>\S+) (?<model>\d+) (?<context>\d+) (?<compute>\d+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UsageLineRegex();

    /// <summary>Parses a MiB count from the CLI output into bytes.</summary>
    private static bool TryParseMib(string value, out ulong bytes)
    {
        bytes = 0;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mib))
            return false;
        bytes = mib * Mebibyte;
        return true;
    }
}
