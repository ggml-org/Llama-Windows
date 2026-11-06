namespace LlamaApp.Common;

/// <summary>The result of parsing a <c>llama://</c> deep link.</summary>
public enum DeepLinkKind
{
    /// <summary>Not a deep link we act on (unknown scheme, host, or payload).</summary>
    None,

    /// <summary>Open the chat overlay — with a prefilled composer when a
    /// prompt is present.</summary>
    Chat,

    /// <summary>Load a server-known model and open the chat on it.</summary>
    LoadModel,

    /// <summary>Download a Hugging Face repo's model (a <c>llama://install/</c>
    /// deep link). A file-specific link preserves its quant; a model-card link
    /// chooses a default/fitting quant.</summary>
    Install,
}

/// <summary>
/// A parsed deep link: exactly one of <see cref="Prompt"/> /
/// <see cref="ModelId"/> / <see cref="Repo"/> is set, per
/// <see cref="DeepLinkKind"/>.
/// </summary>
public sealed record DeepLinkRequest(
    DeepLinkKind Kind, string? Prompt, string? ModelId, string? Repo = null, string? Quant = null)
{
    public static readonly DeepLinkRequest None = new(DeepLinkKind.None, null, null);

    public static DeepLinkRequest ForChat(string? prompt) => new(DeepLinkKind.Chat, prompt, null);

    public static DeepLinkRequest ForModel(string modelId) => new(DeepLinkKind.LoadModel, null, modelId);

    public static DeepLinkRequest ForInstall(string repo, string? quant = null) =>
        new(DeepLinkKind.Install, null, null, repo, quant);
}

/// <summary>
/// Parses <c>llama://</c> protocol activations:
///
/// <list type="bullet">
/// <item><c>llama://chat</c> — open the chat overlay.</item>
/// <item><c>llama://chat?prompt=&lt;urlencoded&gt;</c> — open the overlay with
/// the composer prefilled.</item>
/// <item><c>llama://model/&lt;id&gt;</c> — load a server-known model (e.g.
/// <c>ggml-org%2Fgemma-4-E4B-it-GGUF%3AQ4_0</c>) and open the chat on it.
/// The id may also appear unencoded — the whole remainder of the path is the
/// id, so slashes and colons survive.</item>
/// <item><c>llama://install?repo=&lt;urlencoded&gt;</c> — install a Hugging Face
/// repo's model (e.g. <c>unsloth/flint-GGUF</c>). A model-card link has no
/// <c>quant</c> and uses the default/fitting selection heuristic. A link from
/// a specific GGUF file also carries <c>&amp;quant=&lt;urlencoded&gt;</c>; that
/// explicit selection is preserved and never replaced by the heuristic. The
/// repo must be a safe repo id (see <see cref="IsSafeRepo"/>); a missing or
/// malformed repo yields no request.</item>
/// </list>
///
/// <para>Deep-link payloads are remote-controlled data — any app or website
/// can ask Windows to activate us with an arbitrary URI — so the parser is
/// strict: unknown scheme or host, oversized payloads, and unparsable URIs
/// all yield <see cref="DeepLinkRequest.None"/>. Model ids are not resolved
/// here; the dispatcher matches them against the server's model list before
/// anything is loaded (an id that isn't known to the server is never
/// downloaded or materialized anywhere).</para>
///
/// <para>Pure — unit-testable without a package identity.</para>
/// </summary>
public static class DeepLink
{
    /// <summary>The custom URI scheme declared in the package manifest.</summary>
    public const string Scheme = "llama";

    /// <summary>The <c>chat</c> host — opens the chat overlay.</summary>
    public const string ChatHost = "chat";

    /// <summary>The <c>model</c> host — loads a model and opens the chat.</summary>
    public const string ModelHost = "model";

    /// <summary>The <c>install</c> host — downloads a repo's model.</summary>
    public const string InstallHost = "install";

    /// <summary>Maximum accepted prompt length — beyond this the link is treated as abusive.</summary>
    public const int MaxPromptChars = 64 * 1024;

    /// <summary>Maximum accepted model-id length (server ids are far shorter).</summary>
    public const int MaxModelIdChars = 300;

    /// <summary>Maximum accepted repo-id length for install links (Hub ids are far shorter).</summary>
    public const int MaxRepoChars = 200;

    /// <summary>Maximum accepted explicit quant-label length.</summary>
    public const int MaxQuantChars = 64;

    /// <summary>Parses a protocol-activation URI. Never throws.</summary>
    public static DeepLinkRequest Parse(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return DeepLinkRequest.None;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return DeepLinkRequest.None;
        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return DeepLinkRequest.None;

        var host = parsed.Host?.ToLowerInvariant() ?? ""; // hosts are case-insensitive
        if (string.Equals(host, ChatHost, StringComparison.Ordinal))
            return DeepLinkRequest.ForChat(PromptFromQuery(parsed.Query));

        if (string.Equals(host, ModelHost, StringComparison.Ordinal))
        {
            // The whole remainder of the path is the id (unencoded slashes
            // and colons survive this way); percent-encoded ones decode too.
            var id = Uri.UnescapeDataString(parsed.AbsolutePath).TrimStart('/');
            if (id.Length == 0 || id.Length > MaxModelIdChars) return DeepLinkRequest.None;
            return DeepLinkRequest.ForModel(id);
        }

        if (string.Equals(host, InstallHost, StringComparison.Ordinal))
        {
            try { return InstallFromQuery(parsed.Query); }
            catch (UriFormatException) { return DeepLinkRequest.None; }
        }

        return DeepLinkRequest.None;
    }

    /// <summary>
    /// True when <paramref name="repo"/> is a safe Hub repo id to install:
    /// one or two segments drawn from <c>[A-Za-z0-9._-]</c>, no
    /// <c>.</c>/<c>..</c> segment, no backslash, bounded length — the same
    /// safe-segment rules as server model ids that become cache paths (see
    /// <c>ModelFileLocator.IsSafeModelId</c>). A bare <c>org-less</c> id
    /// (<c>gpt2</c>) is a valid Hub repo and is accepted; three or more
    /// segments, an empty segment, whitespace, a colon, or any other
    /// character is not. The repo string is remote-controlled (a deep link)
    /// or user-writable (the handoff file) and later reaches Hub URLs and
    /// <c>repo:quant</c> model ids — never a filesystem path — but it is
    /// validated here anyway, once, at the trust boundary.
    /// </summary>
    public static bool IsSafeRepo(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return false;
        if (repo.Length > MaxRepoChars) return false;

        var parts = repo.Split('/');
        if (parts.Length is 0 or > 2) return false;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or "..") return false;
            foreach (var c in part)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        }
        return true;
    }

    /// <summary>
    /// Parses the first <c>repo</c> and <c>quant</c> query parameters. An
    /// absent or empty quant denotes a model-card link. A present quant is
    /// syntactically bounded here and semantically matched against Hub file
    /// metadata later; it is NEVER discarded in favor of default selection.
    /// </summary>
    private static DeepLinkRequest InstallFromQuery(string? query)
    {
        if (string.IsNullOrEmpty(query) || query.Length <= 1) return DeepLinkRequest.None;

        string? repo = null;
        string? quant = null;
        var quantSeen = false;
        foreach (var pair in query[1..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var raw = pair[(eq + 1)..];

            if (string.Equals(key, "repo", StringComparison.OrdinalIgnoreCase))
            {
                if (repo is not null) continue; // first repo parameter wins
                if (raw.Length > MaxRepoChars) return DeepLinkRequest.None; // bound before decoding
                var value = Uri.UnescapeDataString(raw);
                if (!IsSafeRepo(value)) return DeepLinkRequest.None;
                repo = value;
            }
            else if (string.Equals(key, "quant", StringComparison.OrdinalIgnoreCase) && !quantSeen)
            {
                quantSeen = true;
                if (raw.Length > MaxQuantChars) return DeepLinkRequest.None;
                var value = Uri.UnescapeDataString(raw);
                if (value.Length == 0) continue; // empty means model-card/default selection
                if (value.Length > MaxQuantChars || !IsSafeQuant(value)) return DeepLinkRequest.None;
                quant = value;
            }
        }

        return repo is null ? DeepLinkRequest.None : DeepLinkRequest.ForInstall(repo, quant);
    }

    // Quant labels are remote-controlled too. Keep them to the HF grammar's
    // character set; whether the label actually exists is checked against the
    // repo's siblings by QuantSelection, never by falling back to a default.
    private static bool IsSafeQuant(string quant) =>
        quant.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>
    /// The first <c>prompt</c> query parameter, percent-decoded, or null when
    /// absent or empty (both mean "just open the chat"). Oversized prompts are
    /// rejected as abusive rather than truncated.
    /// </summary>
    private static string? PromptFromQuery(string? query)
    {
        if (string.IsNullOrEmpty(query) || query.Length <= 1) return null;
        foreach (var pair in query[1..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair[..eq];
            if (!string.Equals(Uri.UnescapeDataString(key), "prompt", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (value.Length > MaxPromptChars) return null;
            return value.Length == 0 ? null : value;
        }
        return null;
    }
}
