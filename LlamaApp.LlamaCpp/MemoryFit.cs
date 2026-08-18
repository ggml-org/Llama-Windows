using System.Globalization;

namespace LlamaApp.Llama;

/// <summary>Where the model would run if it fits.</summary>
public enum FitTarget
{
    /// <summary>Doesn't fit anywhere the probe could see.</summary>
    None,
    /// <summary>Fits in accelerator memory (VRAM) — llama.cpp can also split
    /// across multiple devices, so the budget is the sum of all free VRAM.</summary>
    Gpu,
    /// <summary>Fits in system RAM only — it will run on the CPU (or partially
    /// offloaded when devices exist but their VRAM alone is too small).</summary>
    Cpu,
}

/// <summary>
/// The verdict of a model-fit check: whether the estimated footprint fits,
/// where it would run, and the numbers behind the decision (for logging and
/// user-facing messaging).
/// </summary>
public sealed record MemoryFitResult
{
    /// <summary>True when the estimate fits some target — or when nothing
    /// could be estimated (fail open: an unknown probe never blocks).</summary>
    public required bool Fits { get; init; }

    /// <summary>Where the model would run.</summary>
    public required FitTarget Target { get; init; }

    /// <summary>Estimated total footprint in bytes (0 when unknown).</summary>
    public ulong RequiredBytes { get; init; }

    /// <summary>The budget it was compared against, in bytes: total free
    /// VRAM for <see cref="FitTarget.Gpu"/>, the usable RAM share for
    /// <see cref="FitTarget.Cpu"/>. On a failed fit, the larger of the two
    /// budgets (so messaging can say "only X free").</summary>
    public ulong AvailableBytes { get; init; }

    /// <summary>The accelerator devices the check saw (empty = CPU-only).</summary>
    public IReadOnlyList<LlamaDevice> Devices { get; init; } = [];

    /// <summary>One-line human summary, for logs.</summary>
    public string Details { get; init; } = "";
}

/// <summary>
/// Decides whether a <see cref="MemoryEstimate"/> fits on this machine,
/// from a device probe (<see cref="DeviceQuery"/>) and a RAM probe
/// (<see cref="SystemMemory"/>):
/// <list type="bullet">
/// <item>With accelerator devices: the budget is the <b>sum of free VRAM</b>
/// across all of them (llama.cpp splits layers across devices). Fits →
/// <see cref="FitTarget.Gpu"/>.</item>
/// <item>Doesn't fit VRAM, or no devices at all: fall back to system RAM
/// (a fraction of the currently-available bytes, to leave the OS and the
/// rest of the app breathing room). Fits → <see cref="FitTarget.Cpu"/> —
/// with devices present this means a partially-offloaded run.</item>
/// <item>Unknown estimate, or no devices AND a failed RAM probe: fail open
/// — a guess we don't have must never block a download (same convention as
/// <c>DiskSpace</c>). With devices present but RAM unknown, an exact VRAM
/// budget was checked, so exceeding it stays a conservative "doesn't fit".</item>
/// </list>
/// Pure and static so it stays unit-testable.
/// </summary>
public static class MemoryFit
{
    /// <summary>
    /// Fraction of currently-available system RAM counted as usable for the
    /// model — the OS, the app, and the server itself need the rest, and a
    /// machine that swaps under load is worse than an honest "doesn't fit".
    /// </summary>
    public const double CpuRamBudgetFraction = 0.75;

    /// <summary>
    /// Checks <paramref name="estimate"/> against <paramref name="devices"/>
    /// and <paramref name="availableSystemBytes"/> (null when the RAM probe
    /// failed). See the type docs for the decision rules.
    /// </summary>
    public static MemoryFitResult Check(
        MemoryEstimate estimate,
        IReadOnlyList<LlamaDevice> devices,
        ulong? availableSystemBytes)
    {
        var vramFree = SumFreeVram(devices);
        ulong? ramBudget = availableSystemBytes is { } ram
            ? (ulong)(ram * CpuRamBudgetFraction)
            : null;

        // Nothing to compare — fail open (unknown never blocks).
        if (estimate.IsUnknown)
        {
            return new MemoryFitResult
            {
                Fits = true,
                Target = devices.Count > 0 ? FitTarget.Gpu : FitTarget.Cpu,
                Devices = devices,
                Details = "model size unknown; fit check skipped",
            };
        }

        var required = estimate.TotalBytes;

        if (devices.Count > 0 && required <= vramFree)
        {
            return new MemoryFitResult
            {
                Fits = true,
                Target = FitTarget.Gpu,
                RequiredBytes = required,
                AvailableBytes = vramFree,
                Devices = devices,
                Details = $"{FormatBytes(required)} fits in {FormatBytes(vramFree)} free VRAM " +
                          $"across {devices.Count} device(s): {DeviceNames(devices)}",
            };
        }

        // No accelerator devices AND the RAM probe failed: no budget at all
        // → fail open (a failed probe never blocks, same as DiskSpace).
        // (With devices present an exact VRAM budget was checked above, so a
        // missing RAM number there stays a conservative "doesn't fit".)
        if (devices.Count == 0 && ramBudget is null)
        {
            return new MemoryFitResult
            {
                Fits = true,
                Target = FitTarget.Cpu,
                RequiredBytes = required,
                Devices = devices,
                Details = "no devices and system RAM unknown; fit check skipped",
            };
        }

        if (ramBudget is { } budget && required <= budget)
        {
            return new MemoryFitResult
            {
                Fits = true,
                Target = FitTarget.Cpu,
                RequiredBytes = required,
                AvailableBytes = budget,
                Devices = devices,
                Details = devices.Count > 0
                    ? $"{FormatBytes(required)} exceeds {FormatBytes(vramFree)} free VRAM " +
                      $"but fits in {FormatBytes(budget)} usable RAM (CPU/partial offload)"
                    : $"{FormatBytes(required)} fits in {FormatBytes(budget)} usable RAM (CPU)",
            };
        }

        return new MemoryFitResult
        {
            Fits = false,
            Target = FitTarget.None,
            RequiredBytes = required,
            AvailableBytes = Math.Max(vramFree, ramBudget ?? 0),
            Devices = devices,
            Details = devices.Count > 0
                ? $"{FormatBytes(required)} needed; only {FormatBytes(vramFree)} free VRAM " +
                  $"({DeviceNames(devices)}) and " +
                  $"{(ramBudget is null ? "no" : FormatBytes(ramBudget.Value))} usable RAM"
                : $"{FormatBytes(required)} needed; " +
                  $"{(ramBudget is null ? "system RAM unknown" : $"only {FormatBytes(ramBudget.Value)} usable RAM")}",
        };
    }

    /// <summary>Total free memory across all accelerator devices.</summary>
    internal static ulong SumFreeVram(IReadOnlyList<LlamaDevice> devices)
    {
        ulong sum = 0;
        foreach (var d in devices)
            sum += d.FreeBytes;
        return sum;
    }

    private static string DeviceNames(IReadOnlyList<LlamaDevice> devices) =>
        string.Join(", ", devices.Select(d => d.Name));

    /// <summary>
    /// Formats bytes as a human-readable size using decimal units
    /// (1 GB = 1e9 B) — the same convention as the catalog's size strings,
    /// so a flyout's "needs X" matches the row's size label.
    /// </summary>
    public static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1_000_000_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000_000.0:0.#} TB");
        if (bytes >= 1_000_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000.0:0.#} GB");
        if (bytes >= 1_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0} MB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000.0:0} KB");
    }
}
