using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// The backend class a device belongs to — derived from the device id prefix
/// in the <c>--list-devices</c> output (<c>CUDA0</c>, <c>Vulkan1</c>, …).
/// </summary>
public enum DeviceKind
{
    /// <summary>Unrecognized id prefix.</summary>
    Unknown,
    /// <summary>NVIDIA CUDA.</summary>
    Cuda,
    /// <summary>Cross-vendor Vulkan.</summary>
    Vulkan,
    /// <summary>Apple Metal (never on Windows, but the parser stays generic).</summary>
    Metal,
    /// <summary>The CPU "device" (system RAM).</summary>
    Cpu,
}

/// <summary>
/// A compute device reported by <c>llama --list-devices</c>: its id
/// (<c>CUDA0</c>), name, and memory capacity. Memory is in bytes; the CLI
/// reports MiB and the parser converts.
/// </summary>
public sealed record LlamaDevice
{
    /// <summary>Device id as printed, e.g. <c>CUDA0</c>, <c>Vulkan1</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Device name as printed, e.g. <c>NVIDIA GeForce RTX 4060 Ti</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Total memory on the device, in bytes.</summary>
    public ulong TotalBytes { get; init; }

    /// <summary>Memory currently free on the device, in bytes.</summary>
    public ulong FreeBytes { get; init; }

    /// <summary>Backend class derived from the <see cref="Id"/> prefix.</summary>
    public DeviceKind Kind { get; init; }
}

/// <summary>
/// Probes the compute devices available to llama.cpp by running the CLI's
/// <c>--list-devices</c> command and parsing its output:
///
/// <code>
/// Available devices:
///   CUDA0: NVIDIA GeForce RTX 4060 Ti (15944 MiB, 14143 MiB free)
/// </code>
///
/// or, on a machine without accelerators (or a CPU-only build):
///
/// <code>
/// Available devices:
///   (none)
/// </code>
///
/// An empty result means "no accelerator devices" — the caller then falls
/// back to system CPU/RAM (<see cref="SystemMemory"/>) for fit decisions.
/// </summary>
public static partial class DeviceQuery
{
    /// <summary>
    /// The header line that starts the device section in the CLI output.
    /// </summary>
    internal const string Header = "Available devices:";

    /// <summary>
    /// Runs <c>llama --list-devices</c> and returns the parsed devices (empty
    /// when the output says <c>(none)</c>, the binary fails, or the output is
    /// unrecognizable — a failed probe must never block the caller, matching
    /// the fail-open convention of <see cref="DiskSpace"/>).
    ///
    /// <para>Two argument forms are tried in order: <c>cli --list-devices</c>
    /// (the app-managed launcher wraps the real CLI under a <c>cli</c>
    /// subcommand — a bare <c>list-devices</c> answers "unknown command") and
    /// plain <c>--list-devices</c> (a standalone llama-cli build with no
    /// subcommand layer). A candidate is accepted only when its output
    /// actually contains the <c>Available devices:</c> section — the launcher
    /// exits 0 even for unknown commands, so the exit code alone is not a
    /// signal.</para>
    /// </summary>
    public static async Task<IReadOnlyList<LlamaDevice>> ListDevicesAsync(
        string binaryPath, CancellationToken cancel = default)
    {
        foreach (var args in ArgumentCandidates)
        {
            string? output;
            try
            {
                output = await RunAsync(binaryPath, args, cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"list-devices probe failed ({string.Join(' ', args)}): {ex.Message}");
                continue;
            }

            if (output is not null && output.Contains(Header, StringComparison.Ordinal))
                return Parse(output);
        }

        return [];
    }

    /// <summary>
    /// Argument candidates for the probe, in priority order — see
    /// <see cref="ListDevicesAsync"/> for why both exist.
    /// </summary>
    private static readonly string[][] ArgumentCandidates =
    [
        ["cli", "--list-devices"],
        ["--list-devices"],
    ];

    /// <summary>
    /// Runs the binary with <paramref name="args"/> and returns its combined
    /// stdout + stderr (the CLI's init noise — <c>load_backend</c>,
    /// <c>ggml_cuda_init</c> — can land on either stream depending on the
    /// build, and the device section must survive both). Bounded by a 10s
    /// timeout; a hung probe is killed and reported as <c>null</c>.
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
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
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
    /// Parses a <c>--list-devices</c> output (stdout + stderr combined) into
    /// devices. Only lines inside the <c>Available devices:</c> section are
    /// considered — everything before it (backend init noise, version banners)
    /// is ignored, and the section ends at the first non-indented line.
    /// <c>(none)</c> yields an empty list. Internal for unit tests.
    /// </summary>
    internal static IReadOnlyList<LlamaDevice> Parse(string output)
    {
        var devices = new List<LlamaDevice>();

        var inSection = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (!inSection)
            {
                if (line.TrimEnd().EndsWith(Header, StringComparison.Ordinal))
                    inSection = true;
                continue;
            }

            // The section is indented; a non-empty, non-indented line ends it.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed == "(none)")
                continue;

            var match = DeviceLineRegex().Match(trimmed);
            if (!match.Success)
                continue;

            if (!TryParseSize(match.Groups["total"].Value, match.Groups["tunit"].Value, out var total) ||
                !TryParseSize(match.Groups["free"].Value, match.Groups["funit"].Value, out var free))
                continue;

            var id = match.Groups["id"].Value;
            devices.Add(new LlamaDevice
            {
                Id = id,
                Name = match.Groups["name"].Value.Trim(),
                TotalBytes = total,
                FreeBytes = free,
                Kind = KindFromId(id),
            });
        }

        return devices;
    }

    /// <summary>
    /// One device entry, e.g.
    /// <c>CUDA0: NVIDIA GeForce RTX 4060 Ti (15944 MiB, 14143 MiB free)</c>.
    /// </summary>
    [GeneratedRegex(
        @"^(?<id>[A-Za-z]+\d*):\s*(?<name>.+?)\s*\((?<total>[\d.]+)\s*(?<tunit>[KMGT]?i?B),\s*(?<free>[\d.]+)\s*(?<funit>[KMGT]?i?B)\s*free\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLineRegex();

    /// <summary>
    /// Maps a device id prefix to its backend class.
    /// </summary>
    internal static DeviceKind KindFromId(string id)
    {
        if (id.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Cuda;
        if (id.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Vulkan;
        if (id.StartsWith("Metal", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Metal;
        if (id.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Cpu;
        return DeviceKind.Unknown;
    }

    /// <summary>
    /// Parses a size token from the CLI output (<c>15944 MiB</c>,
    /// <c>1.5 GiB</c>) into bytes. Binary units (<c>KiB/MiB/GiB/TiB</c>) use
    /// 1024 steps; decimal units (<c>KB/MB/GB/TB</c> and bare <c>B</c>) use
    /// 1000. <c>false</c> on malformed input.
    /// </summary>
    internal static bool TryParseSize(string value, string unit, out ulong bytes)
    {
        bytes = 0;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0)
            return false;

        ulong factor;
        switch (unit.ToUpperInvariant())
        {
            case "B": factor = 1; break;
            case "KB": factor = 1_000; break;
            case "MB": factor = 1_000_000; break;
            case "GB": factor = 1_000_000_000; break;
            case "TB": factor = 1_000_000_000_000; break;
            case "KIB": factor = 1UL << 10; break;
            case "MIB": factor = 1UL << 20; break;
            case "GIB": factor = 1UL << 30; break;
            case "TIB": factor = 1UL << 40; break;
            default: return false;
        }

        bytes = (ulong)(number * factor);
        return true;
    }
}
