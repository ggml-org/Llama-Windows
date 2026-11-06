using System.Text.RegularExpressions;
using LlamaApp.HuggingFace;
using LlamaApp.Llama;

namespace LlamaApp;

/// <summary>
/// The pure core of the <c>llama://install</c> flow: from a repo's GGUF
/// files (with sizes) it derives the downloadable builds (one per quant) and
/// chooses which quant to request — no HTTP, no XAML, no hardware probes, so
/// the choice is fully unit-testable (the caller supplies the budget).
///
/// <para><b>The model-card selection rule</b> (<see cref="SelectQuant"/>):
/// an explicit file-link quant always wins verbatim, without a fit heuristic.
/// Otherwise, on a KNOWN memory budget, each build's fit is classified with
/// <see cref="HardwareFitHeuristic"/> and a fitting Q4_K_M/Q4_0 is preferred;
/// absent a recommended quant, the <b>largest</b> Fit-or-Might build wins.
/// If NOTHING fits on a known budget, the selector returns null so the caller
/// refuses rather than downloading a build the machine can't run. On an
/// UNKNOWN budget (probe failed/unavailable) or when the sizes can't be
/// ranked, it falls back to a <b>conservative known quant</b> — the
/// <see cref="ConservativeLadder"/> tag the repo ships, else the smallest
/// measurable build, else the deterministic first nameable build (never null
/// for a non-empty list — the caller never requests a quant blindly).</para>
///
/// <para>Pure and static so it stays unit-testable, mirroring
/// <see cref="HardwareFitHeuristic"/>.</para>
/// </summary>
internal static class QuantSelection
{
    /// <summary>
    /// The conservative quant requested when the selector can't identify a
    /// build: the well-known low-risk default (Q4_K_M — llama.cpp's own
    /// default download tag and the Mac app's first preference), requested
    /// as <c>repo:Q4_K_M</c> when the repo doesn't even list it.
    /// </summary>
    internal const string ConservativeQuant = "Q4_K_M";

    /// <summary>
    /// The conservative-quant ladder for the fallback/default pick: the
    /// first tag the repo ships wins — Q4_K_M (llama.cpp's default download
    /// tag), then Q4_0 (the Mac app's <c>selectMain</c> preference pair; a
    /// high-fidelity quant like Q8_0 is deliberately NOT in the ladder —
    /// "conservative" here means conservative about fitting this machine).
    /// </summary>
    private static readonly string[] ConservativeLadder =
        [ConservativeQuant, "Q4_0"];

    /// <summary>
    /// Chooses the quant to install from a repo's <paramref name="builds"/>.
    /// A non-empty <paramref name="requestedQuant"/> came from a specific GGUF
    /// file link: match that exact HF label (including UD-/size variants), do
    /// not fit-rank it, and return null rather than substituting another quant.
    /// Model-card links omit it and use <paramref name="budgetBytes"/> as
    /// described above. Null means no such explicit build, an empty list, or a
    /// KNOWN budget where every default candidate is NoWay.
    /// </summary>
    internal static Repository? SelectQuant(
        IReadOnlyList<Repository> builds, ulong budgetBytes, string? requestedQuant = null)
    {
        if (builds.Count == 0) return null;
        if (!string.IsNullOrEmpty(requestedQuant))
            return SelectExplicit(builds, requestedQuant);

        // An unknown budget (0 = probe failed, ulong.MaxValue = no devices and
        // no RAM number) carries no fit signal: the largest pick would be pure
        // size (F32) on a measurement error, so go conservative instead.
        if (budgetBytes == 0 || budgetBytes == ulong.MaxValue)
            return ConservativePick(builds);

        var acceptable = builds
            .Where(b => HardwareFitHeuristic.Classify(b.SizeBytes, budgetBytes) != HardwareFitLevel.NoWay)
            .ToList();
        if (acceptable.Count == 0)
            return null; // known budget, nothing fits — the caller must refuse

        // Recommended default first; otherwise largest-fitting. Builds whose
        // size we know outrank unknown-size builds (handled below).
        var measurable = acceptable.Where(b => b.SizeBytes > 0).ToList();
        if (measurable.Count > 0)
            return PreferredPick(measurable) ?? BySize(measurable, largest: true);

        // Acceptable, but no size to rank by — conservative default.
        return ConservativePick(acceptable);
    }

    /// <summary>
    /// Resolves an explicit HF quant label without applying memory fit. Exact
    /// labels win first, so Q4_K_M and UD-Q4_K_M (or size-suffixed variants)
    /// cannot be interchanged when both exist. For compatibility with manual
    /// repo:quant links, a plain canonical tag may resolve to a prefixed build
    /// only when no exact plain build exists.
    /// </summary>
    private static Repository? SelectExplicit(IReadOnlyList<Repository> builds, string requestedQuant)
    {
        var requested = NormalizeExplicitQuant(requestedQuant);
        if (requested is null) return null;

        var exact = builds
            .Where(b => string.Equals(NormalizeExplicitQuant(b.Quant ?? ""), requested,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0) return BySize(exact, largest: true);

        var canonical = ExtractQuantTag(requested);
        if (canonical is null || !string.Equals(canonical, requested, StringComparison.Ordinal))
            return null; // a prefixed/suffixed request must match that exact file label

        var compatible = builds
            .Where(b => string.Equals(ExtractQuantTag(b.Quant ?? ""), canonical,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        return compatible.Count > 0 ? BySize(compatible, largest: true) : null;
    }

    /// <summary>The recommended fitting build, or null when the pool ships neither tag.</summary>
    private static Repository? PreferredPick(IReadOnlyList<Repository> pool)
    {
        foreach (var tag in ConservativeLadder)
        {
            var tagged = pool
                .Where(b => string.Equals(ExtractQuantTag(b.Quant ?? ""), tag,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tagged.Count > 0) return BySize(tagged, largest: true);
        }
        return null;
    }

    /// <summary>
    /// Conservative fallback for a non-empty pool: the first well-known ladder
    /// tag the pool ships (<see cref="ConservativeLadder"/>), else the smallest
    /// measurable build, else the deterministic first nameable build. Never
    /// null for a non-empty pool — the caller must not request
    /// <c>repo:Q4_K_M</c> blindly when the repo doesn't ship it.
    /// </summary>
    private static Repository ConservativePick(IReadOnlyList<Repository> pool)
    {
        if (PreferredPick(pool) is { } preferred) return preferred;

        var known = pool.Where(b => b.SizeBytes > 0).ToList();
        if (known.Count > 0)
            return BySize(known, largest: false);

        return pool
            .OrderBy(b => b.Quant, StringComparer.Ordinal)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// The deterministic pick from <paramref name="pool"/>: largest (or
    /// smallest) by size, ties broken by quant tag then repo name — so the
    /// same input always yields the same download.
    /// </summary>
    private static Repository BySize(List<Repository> pool, bool largest) => largest
        ? pool.OrderByDescending(b => b.SizeBytes)
            .ThenBy(b => b.Quant, StringComparer.Ordinal)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .First()
        : pool.OrderBy(b => b.SizeBytes)
            .ThenBy(b => b.Quant, StringComparer.Ordinal)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .First();

    /// <summary>
    /// Groups a repo's files (from
    /// <see cref="HubClient.ListRepoFileSizesAsync"/>) into one
    /// <see cref="Repository"/> build per quant: main-weight GGUFs only
    /// (sidecars excluded), shard sets summed into their quant's total size,
    /// quant tags parsed from the filenames. Builds are returned sorted by
    /// tag for determinism. Files without a recognizable quant tag are
    /// skipped — they can't be named in the <c>repo:quant</c> form the
    /// server download requires.
    /// </summary>
    internal static List<Repository> BuildsFor(string repoId, IReadOnlyList<HubClient.RepoFile> files)
    {
        var groups = new SortedDictionary<string, (ulong Size, string MainPath)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = BaseName(file.Path);
            if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsSidecar(name)) continue;
            // Keep the complete HF label (UD- prefix and size variation), not
            // just its canonical tag: an explicit file link must select that
            // exact label, and llama-server's repo:quant matcher accepts it.
            var tag = ExtractQuantLabel(name);
            if (tag is null) continue;

            if (groups.TryGetValue(tag, out var group))
                groups[tag] = (group.Size + file.SizeBytes,
                    string.CompareOrdinal(file.Path, group.MainPath) < 0 ? file.Path : group.MainPath);
            else
                groups[tag] = (file.SizeBytes, file.Path);
        }

        return groups.Select(g => new Repository
        {
            Name = repoId,
            Quant = g.Key,
            Description = "",
            License = "",
            Parameters = "",
            Size = g.Value.Size > 0 ? MemoryFit.FormatBytes(g.Value.Size) : "",
            SizeBytes = g.Value.Size,
            Vision = false,
        }).ToList();
    }

    /// <summary>
    /// True for files that are never a main weight: <c>mmproj</c> sidecars,
    /// imatrix calibration files, and standalone draft-head sidecars
    /// (mtp-/dflash-/dspark- — the <c>DraftHeads.SidecarPrefixes</c> list —
    /// plus the Mac app's <c>eagle3-</c>). Shards are NOT sidecars: they are summed into their
    /// quant's build by <see cref="BuildsFor"/>.
    /// </summary>
    private static bool IsSidecar(string name)
    {
        if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("imatrix", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in new[] { "mtp-", "dflash-", "dspark-", "eagle3-" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The quant tag a GGUF filename carries, e.g. <c>model-Q4_K_M.gguf</c> →
    /// <c>Q4_K_M</c>, <c>GLM-4.5-Air-UD-Q4_K_M.gguf</c> → <c>Q4_K_M</c>
    /// (unknown tokens like <c>UD</c>/<c>XXL</c> are skipped), or null when
    /// no known tag appears. The LAST HF label match wins (the quant is
    /// conventionally the final label). Canonical uppercase output so
    /// <c>repo:&lt;tag&gt;</c> ids are consistent regardless of the casing
    /// the repo's filenames use; UD- is removed, while a size variation such
    /// as <c>Q4_K_M_XXL</c> remains part of the tag.
    /// </summary>
    internal static string? ExtractQuantTag(string fileName)
    {
        var label = ExtractQuantLabel(fileName);
        return label is not null && label.StartsWith("UD-", StringComparison.Ordinal)
            ? label[3..]
            : label;
    }

    /// <summary>
    /// The complete HF quant label in a filename or explicit quant parameter:
    /// canonical casing, optional <c>UD-</c> prefix and optional alphabetic
    /// size variation retained. The last match wins, mirroring Hugging Face's
    /// <c>parseGGUFQuantLabel</c> (which is what creates file-specific links).
    /// </summary>
    internal static string? ExtractQuantLabel(string fileName)
    {
        var matches = QuantLabelRegex.Matches(BaseName(fileName));
        return matches.Count == 0 ? null : matches[^1].Value.ToUpperInvariant();
    }

    /// <summary>Validates and canonicalizes a standalone explicit quant label.</summary>
    private static string? NormalizeExplicitQuant(string quant)
    {
        var match = ExplicitQuantLabelRegex.Match(quant);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>The tail of a repo-relative file path (both separators).</summary>
    private static string BaseName(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>
    /// HF's GGUF label grammar. Alternation order mirrors huggingface.js (and
    /// therefore its deep links), including its historical Q4_0/Q4_0_4_4
    /// leftmost-match behavior. MXFP4 is the filename alias used by gpt-oss.
    /// </summary>
    private const string QuantLabelPattern =
        @"(?:UD-)?(?:F32|F16|Q4_0|Q4_1|Q4_1_SOME_F16|Q4_2|Q4_3|Q8_0|Q8_1|Q8_K_S|Q8_K_M|" +
        @"Q5_0|Q5_1|Q2_K|Q3_K_S|Q3_K_M|Q3_K_L|Q4_K_S|Q4_K_M|Q5_K_S|Q5_K_M|Q6_K|" +
        @"IQ2_XXS|IQ2_XS|Q2_K_S|IQ3_XS|IQ3_XXS|IQ1_S|IQ4_NL|IQ3_S|IQ3_M|IQ2_S|IQ2_M|" +
        @"IQ4_XS|IQ1_M|BF16|Q4_0_4_4|Q4_0_4_8|Q4_0_8_8|TQ1_0|TQ2_0|Q2_0|F8_E4M3|" +
        @"MXFP4_MOE|MXFP4|NVFP4|Q1_0|Q2_K_XL|Q3_K_XL|Q4_K_XL|Q5_K_XL|Q6_K_XL|Q8_K_XL)(?:_[A-Z]+)?";

    private static readonly Regex QuantLabelRegex = new(
        QuantLabelPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitQuantLabelRegex = new(
        $"^(?:{QuantLabelPattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
