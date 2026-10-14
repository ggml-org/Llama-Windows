namespace LlamaApp.Common;

/// <summary>
/// A speculative-decoding draft head a model repo can ship: a small extra
/// network that drafts the next few tokens cheaply, which llama-server then
/// verifies against the main weights in one pass — generation gets up to
/// ~2x faster with identical output.
///
/// <para>Repos ship heads as <b>sidecar</b> GGUFs named by their kind's
/// prefix (<c>mtp-….gguf</c>, <c>dflash-….gguf</c>, <c>dspark-….gguf</c>),
/// matching the prefixes llama.cpp itself keys on when it resolves siblings
/// (<c>draft_prefixes</c> in <c>preset.cpp</c>; <c>find_best_mtp/_dflash/_dspark</c>
/// in <c>download.cpp</c>). An MTP head can also be <b>embedded</b> in the
/// main weights, which those builds tag into the main file's name
/// (<c>…-Q4_K_M-mtp.gguf</c>).</para>
///
/// <para>Pure string classification — no I/O — so the detection rules and
/// the preference order are unit-testable.</para>
/// </summary>
public enum DraftHeadKind
{
    /// <summary>Block-diffusion drafting: the head fills a whole block of
    /// tokens at once. Measured 35–50% faster than MTP on Qwen3.8-27B
    /// (llama.cpp b11429, M5 Max) and recommended by the maintainers over
    /// MTP whenever a repo ships both.</summary>
    Dflash,

    /// <summary>Block-diffusion drafting plus a Markov head, which
    /// llama.cpp ranks above plain DFlash ("its sidecar carries the extra
    /// Markov head").</summary>
    Dspark,

    /// <summary>Multi-token prediction: the head predicts a few tokens
    /// ahead.</summary>
    Mtp,
}

/// <summary>
/// Detects the speculative-decoding draft heads a model ships, from repo
/// file listings and file names, and maps a kind to the llama.cpp
/// <c>--spec-type</c> value that engages it. Pure.
/// </summary>
public static class DraftHeads
{
    /// <summary>Sidecar filename prefixes, in llama.cpp's spelling
    /// (<c>preset.cpp: draft_prefixes[] = { "mtp-", "dspark-", "dflash-" }</c>).</summary>
    internal static readonly IReadOnlyList<(DraftHeadKind Kind, string Prefix)> SidecarPrefixes =
    [
        (DraftHeadKind.Dspark, "dspark-"),
        (DraftHeadKind.Dflash, "dflash-"),
        (DraftHeadKind.Mtp, "mtp-"),
    ];

    /// <summary>The <c>--spec-type</c> value engaging this head
    /// (<c>speculative_type_from_str</c> in llama.cpp).</summary>
    public static string SpecType(DraftHeadKind kind) => kind switch
    {
        DraftHeadKind.Dspark => "draft-dspark",
        DraftHeadKind.Dflash => "draft-dflash",
        DraftHeadKind.Mtp => "draft-mtp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The best draft head a repo file listing ships, or <c>null</c> when it
    /// ships none. Preference: DSpark > DFlash > MTP — DSpark's sidecar
    /// carries DFlash's block head plus a Markov head (llama.cpp ranks it
    /// above DFlash), and DFlash was measured 35–50% faster than MTP with
    /// the maintainers recommending it whenever both ship. A sidecar is any
    /// <c>.gguf</c> whose (lowercased) name starts with a kind's prefix.
    /// </summary>
    public static DraftHeadKind? DetectSidecar(IEnumerable<string> repoFiles)
    {
        // The listing order is not meaningful (HF tree order), so the
        // preference is decided by kind, not by which file appeared first.
        foreach (var (kind, prefix) in SidecarPrefixes)
        {
            foreach (var file in repoFiles)
            {
                if (file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                    file.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
                {
                    return kind;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// True when a main model file's name tags an <b>embedded</b> MTP head
    /// — builds with the head baked into the main weights name themselves
    /// with an <c>mtp</c> token (<c>…-Q4_K_M-mtp.gguf</c>,
    /// <c>…-MTP-Q8_0.gguf</c>), matching the Mac app's
    /// <c>ResolvedPaths.usesMTP</c> rule. The <c>mtp-</c> <i>sidecar</i>
    /// prefix (file starts with it) is a separate file and does not tag the
    /// main weights.
    /// </summary>
    public static bool HasEmbeddedMtp(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && name.Contains("-mtp", StringComparison.Ordinal);
    }

    /// <summary>
    /// The repo part of a server model id: <c>owner/repo:quant</c> →
    /// <c>owner/repo</c>. The quant tag follows the last ':', which must come
    /// after the owner/repo '/' — a bare id (a models-dir import name) has
    /// neither and is returned unchanged.
    /// </summary>
    public static string RepoFromModelId(string modelId)
    {
        var colon = modelId.LastIndexOf(':');
        return colon > modelId.IndexOf('/') ? modelId[..colon] : modelId;
    }
}
