namespace LlamaApp.Llama;

/// <summary>
/// The three-level verdict of a hardware fit check — the honest middle
/// ground between a binary "fits / doesn't fit" and a precise number nobody
/// can promise from catalog metadata:
/// <list type="bullet">
/// <item><see cref="Fit"/> — comfortably within this machine's memory
/// budget; download away.</item>
/// <item><see cref="Might"/> — tight, slightly over, or simply unknown;
/// it may run (smaller context, fewer background apps) or it may not.</item>
/// <item><see cref="NoWay"/> — past the budget by more than the
/// estimator's error bars; this machine cannot run it.</item>
/// </list>
/// </summary>
public enum HardwareFitLevel
{
    /// <summary>Comfortably within the memory budget — green.</summary>
    Fit,
    /// <summary>Tight, slightly over, or unknown — yellow.</summary>
    Might,
    /// <summary>Over the budget by more than the error bars — red.</summary>
    NoWay,
}

/// <summary>
/// The verdict of a <see cref="HardwareFitHeuristic"/> check: the level,
/// the numbers behind it (for logging), and the one-line user-facing note
/// (for tooltips) — same shape as <see cref="MemoryFitResult"/>.
/// </summary>
public sealed record HardwareFitVerdict
{
    /// <summary>The three-level verdict.</summary>
    public required HardwareFitLevel Level { get; init; }

    /// <summary>Estimated total footprint in bytes (0 when unknown).</summary>
    public ulong RequiredBytes { get; init; }

    /// <summary>The memory budget it was compared against, in bytes:
    /// free VRAM + the usable share of system RAM (0 when the hardware
    /// probe failed).</summary>
    public ulong BudgetBytes { get; init; }

    /// <summary>The accelerator devices the check saw (empty = CPU-only).</summary>
    public IReadOnlyList<LlamaDevice> Devices { get; init; } = [];

    /// <summary>One-line human summary — the tooltip text; also logged.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// The heuristic behind the green/yellow/red fit coloring of model
/// variations (f32, q8_0, q4_0, …): from the catalog metadata alone
/// (params, quant, GGUF size) it says whether a build would fit this
/// machine — before anything is downloaded or loaded.
///
/// <para><b>The budget</b> is the combined pool llama.cpp can actually
/// use: the free VRAM summed across all accelerator devices (layers split
/// across devices) PLUS the usable share of free system RAM
/// (<see cref="MemoryFit.CpuRamBudgetFraction"/>) — a loaded model can
/// split its layers between GPU and host (partial offload), so the two
/// pools add up, exactly like <c>LlamaManager.ContextMemoryBudgetAsync</c>.</para>
///
/// <para><b>The bands</b> compare the estimated footprint
/// (<see cref="MemoryEstimate.TotalBytes"/>) against that budget:</para>
/// <list type="bullet">
/// <item><b>Fit</b> (green): footprint ≤ 90% of the budget — the 10%
/// slack covers the estimator's error bars and headroom for a longer
/// context. The note says where it will run (full GPU offload when it also
/// fits free VRAM, otherwise split/CPU).</item>
/// <item><b>Might</b> (yellow): ≤ 125% of the budget. Inside the budget
/// it's a tight squeeze (little headroom); just outside it, the footprint
/// is within the estimator's error bars — a smaller context or closing
/// background apps can still make it load, and swapping can make it crawl.
/// Unknowns (no parseable size, failed hardware probe) also land here:
/// "might" is the honest verdict for a guess we don't have, and it never
/// blocks (fail open, same convention as <c>DiskSpace</c>).</item>
/// <item><b>NoWay</b> (red): past even the optimistic band — more than
/// this machine can give by more than the estimate's margin of error.</item>
/// </list>
///
/// <para>The 125% ceiling on optimism is deliberate: the weights estimate
/// is accurate (it IS the file size when known), so a footprint far past
/// the budget is not an estimation artifact.</para>
///
/// <para>Pure and static so it stays unit-testable, mirroring
/// <see cref="MemoryFit"/>.</para>
/// </summary>
public static class HardwareFitHeuristic
{
    /// <summary>
    /// Fit when the footprint is at most this fraction of the budget —
    /// the slack below 100% absorbs estimator error and leaves the OS and
    /// the server itself room to breathe.
    /// </summary>
    public const double ComfortFactor = 0.9;

    /// <summary>
    /// Might (rather than NoWay) while the footprint is at most this
    /// fraction of the budget — the estimator's error bars, plus the
    /// tricks that can still save a tight load (smaller context, closing
    /// apps, letting the OS page out what it can).
    /// </summary>
    public const double OptimismFactor = 1.25;

    /// <summary>
    /// The band decision itself: <paramref name="requiredBytes"/> against
    /// <paramref name="budgetBytes"/> — one place for the thresholds, shared
    /// by the catalog-metadata check and the context-length ladder (whose
    /// estimates are per-context). An unknown requirement (0) or an unknown
    /// budget (0) lands on <see cref="HardwareFitLevel.Might"/>: never a hard
    /// "no way" on a guess we don't have.
    /// </summary>
    public static HardwareFitLevel Classify(ulong requiredBytes, ulong budgetBytes)
    {
        if (requiredBytes == 0 || budgetBytes == 0)
            return HardwareFitLevel.Might;
        if (requiredBytes <= budgetBytes * ComfortFactor)
            return HardwareFitLevel.Fit;
        if (requiredBytes <= budgetBytes * OptimismFactor)
            return HardwareFitLevel.Might;
        return HardwareFitLevel.NoWay;
    }

    /// <summary>
    /// Classifies <paramref name="estimate"/> against
    /// <paramref name="devices"/> (accelerator probe) and
    /// <paramref name="availableSystemBytes"/> (null when the RAM probe
    /// failed). See the type docs for the decision rules.
    /// </summary>
    public static HardwareFitVerdict Classify(
        MemoryEstimate estimate,
        IReadOnlyList<LlamaDevice> devices,
        ulong? availableSystemBytes)
    {
        var vram = MemoryFit.SumFreeVram(devices);
        var ramBudget = availableSystemBytes is { } ram
            ? (ulong)(ram * MemoryFit.CpuRamBudgetFraction)
            : 0UL;
        var budget = vram + ramBudget;

        // Unknown model size: "might" is the honest verdict — fail open,
        // never block a download on a guess we don't have.
        if (estimate.IsUnknown)
        {
            return new HardwareFitVerdict
            {
                Level = HardwareFitLevel.Might,
                BudgetBytes = budget,
                Devices = devices,
                Note = "Might fit: the catalog doesn't say how big this one is.",
            };
        }

        // Nothing to compare against (no devices AND a failed RAM probe):
        // unknown hardware, same fail-open rule.
        if (budget == 0)
        {
            return new HardwareFitVerdict
            {
                Level = HardwareFitLevel.Might,
                RequiredBytes = estimate.TotalBytes,
                Devices = devices,
                Note = "Might fit: this machine's free memory couldn't be measured.",
            };
        }

        var required = estimate.TotalBytes;
        var level = Classify(required, budget);

        if (level == HardwareFitLevel.Fit)
        {
            var where = devices.Count > 0 && required <= vram
                ? $"runs fully in {MemoryFit.FormatBytes(vram)} of GPU memory"
                : devices.Count > 0
                    ? "runs split across GPU memory and system RAM"
                    : "runs in system memory";
            return new HardwareFitVerdict
            {
                Level = HardwareFitLevel.Fit,
                RequiredBytes = required,
                BudgetBytes = budget,
                Devices = devices,
                Note = $"Fits: needs about {MemoryFit.FormatBytes(required)}, {where}.",
            };
        }

        if (level == HardwareFitLevel.Might)
        {
            var tight = required <= budget;
            return new HardwareFitVerdict
            {
                Level = HardwareFitLevel.Might,
                RequiredBytes = required,
                BudgetBytes = budget,
                Devices = devices,
                Note = tight
                    ? $"Tight fit: needs about {MemoryFit.FormatBytes(required)}, " +
                      $"only {MemoryFit.FormatBytes(budget)} usable — little headroom for longer chats."
                    : $"Might fit: needs about {MemoryFit.FormatBytes(required)}, " +
                      $"only {MemoryFit.FormatBytes(budget)} usable — a smaller version or " +
                      "closing other apps may make it fit.",
            };
        }

        return new HardwareFitVerdict
        {
            Level = HardwareFitLevel.NoWay,
            RequiredBytes = required,
            BudgetBytes = budget,
            Devices = devices,
            Note = $"Won't fit: needs about {MemoryFit.FormatBytes(required)}, " +
                   $"but only {MemoryFit.FormatBytes(budget)} is usable on this machine.",
        };
    }
}
