using LlamaApp.HuggingFace;
using LlamaApp.Llama;

namespace LlamaApp;

/// <summary>
/// The pure core of the <c>llama://install</c> flow: from a repo's GGUF
/// files (with sizes) it derives the downloadable builds (one per quant) and
/// chooses which quant to request — no HTTP, no XAML, no hardware probes, so
/// the choice is fully unit-testable (the caller supplies the budget).
///
/// <para><b>The selection rule</b> (<see cref="SelectQuant"/>): each build's
/// fit is classified with <see cref="HardwareFitHeuristic"/> against the
/// memory budget (free VRAM + the usable share of RAM, as everywhere else),
/// and among the builds whose fit is <b>Fit or Might</b> the <b>largest</b>
/// one wins — the highest-fidelity quant this machine can plausibly run
/// (Might is the honest "it may run" band; NoWay is never picked). When
/// nothing fits, or the sizes are unknown and nothing can be ranked, the
/// pick falls back to a <b>conservative known quant</b> —
/// <see cref="ConservativeQuant"/> (Q4_K_M, llama.cpp's default download tag)
/// when the repo ships it, else Q4_0 (the Mac app's fallback pair) — and only
/// if the repo ships neither (and no size is measurable) does the selector
/// return null, for the caller to request <see cref="ConservativeQuant"/>
/// blindly.</para>
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
    /// tag), then Q4_0 (the Mac app's <c>selectMain</c> fallback pair; a
    /// high-fidelity quant like Q8_0 is deliberately NOT in the ladder —
    /// "conservative" here means conservative about fitting this machine).
    /// </summary>
    private static readonly string[] ConservativeLadder =
        [ConservativeQuant, "Q4_0"];

    /// <summary>
    /// Chooses the quant to install from a repo's <paramref name="builds"/>
    /// (one per quant — see <see cref="BuildsFor"/>) against a memory
    /// <paramref name="budgetBytes"/> (free VRAM + the usable share of RAM;
    /// 0 when the hardware probe failed).
    ///
    /// <para>Rule, in order:</para>
    /// <list type="number">
    /// <item>Fit per build = <see cref="HardwareFitHeuristic.Classify(ulong, ulong)"/>
    /// of its size against the budget. Fit and Might are acceptable (an
    /// unknown size or an unknown budget lands on Might — the heuristic's
    /// fail-open convention); NoWay is excluded.</item>
    /// <item><b>Largest-fitting:</b> among the acceptable builds with a
    /// known size, the largest one is picked (highest fidelity that still
    /// plausibly runs; ties broken by quant tag then name for determinism).
    /// Unknown-size builds rank below every measurable one.</item>
    /// <item><b>None-fits fallback / unknown-sizes default:</b> when no
    /// acceptable build is measurable (nothing fits, or the sizes are
    /// unknown), fall back to a conservative known quant — the first
    /// <see cref="ConservativeLadder"/> tag the pool offers (the pool is the
    /// acceptable builds when there are any, else all builds), else the
    /// smallest measurable build.</item>
    /// <item>Null only when there is nothing to name: an empty list, or no
    /// ladder tag and no measurable build at all — the caller then requests
    /// <c>repo:</c><see cref="ConservativeQuant"/>.</item>
    /// </list>
    /// </summary>
    internal static Repository? SelectQuant(IReadOnlyList<Repository> builds, ulong budgetBytes)
    {
        if (builds.Count == 0) return null;

        var acceptable = builds
            .Where(b => HardwareFitHeuristic.Classify(b.SizeBytes, budgetBytes) != HardwareFitLevel.NoWay)
            .ToList();

        // Largest-fitting: the largest acceptable build whose size we know.
        var measurable = acceptable.Where(b => b.SizeBytes > 0).ToList();
        if (measurable.Count > 0)
            return BySize(measurable, largest: true);

        // Nothing measurable passed the fit test — fall back to the
        // conservative known quant (a well-known safe choice), then to the
        // smallest measurable build, then to null.
        var pool = acceptable.Count > 0 ? acceptable : builds;
        foreach (var tag in ConservativeLadder)
        {
            var tagged = pool
                .Where(b => string.Equals(b.Quant, tag, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tagged.Count > 0)
                return BySize(tagged, largest: true);
        }

        var known = pool.Where(b => b.SizeBytes > 0).ToList();
        return known.Count > 0 ? BySize(known, largest: false) : null;
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
            var tag = ExtractQuantTag(name);
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
    /// no known tag appears. The LAST known token wins (the quant is
    /// conventionally the final label). Canonical uppercase output so
    /// <c>repo:&lt;tag&gt;</c> ids are consistent regardless of the casing
    /// the repo's filenames use.
    /// </summary>
    internal static string? ExtractQuantTag(string fileName)
    {
        var name = BaseName(fileName);
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            name = name[..^".gguf".Length];

        var tokens = name.Split('-', '.');
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            var token = tokens[i].ToUpperInvariant();
            if (KnownQuantTags.Contains(token)) return token;
        }
        return null;
    }

    /// <summary>The tail of a repo-relative file path (both separators).</summary>
    private static string BaseName(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>
    /// The known quant labels (the <c>ModelMemoryEstimator.BitsPerWeight</c>
    /// key set plus <c>BF16</c> and <c>Q8_K_XL</c>): anything not in this
    /// table is treated as a non-quant token in a filename.
    /// </summary>
    private static readonly HashSet<string> KnownQuantTags =
    [
        "F32", "F16", "BF16",
        "Q8_0", "Q8_1", "Q8_K_S", "Q8_K_M", "Q8_K_XL",
        "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q5_1",
        "Q4_K_M", "Q4_K_S", "Q4_0", "Q4_1",
        "Q3_K_L", "Q3_K_M", "Q3_K_S", "Q2_K",
        "IQ4_NL", "IQ4_XS", "IQ3_M", "IQ3_S", "IQ3_XS",
        "IQ2_S", "IQ2_XS", "IQ2_XXS", "IQ1_M", "IQ1_S",
        "MXFP4",
    ];
}
