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
}

/// <summary>
/// A parsed deep link: exactly one of <see cref="Prompt"/> /
/// <see cref="ModelId"/> is set, per <see cref="DeepLinkKind"/>.
/// </summary>
public sealed record DeepLinkRequest(DeepLinkKind Kind, string? Prompt, string? ModelId)
{
    public static readonly DeepLinkRequest None = new(DeepLinkKind.None, null, null);

    public static DeepLinkRequest ForChat(string? prompt) => new(DeepLinkKind.Chat, prompt, null);

    public static DeepLinkRequest ForModel(string modelId) => new(DeepLinkKind.LoadModel, null, modelId);
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

    /// <summary>Maximum accepted prompt length — beyond this the link is treated as abusive.</summary>
    public const int MaxPromptChars = 64 * 1024;

    /// <summary>Maximum accepted model-id length (server ids are far shorter).</summary>
    public const int MaxModelIdChars = 300;

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

        return DeepLinkRequest.None;
    }

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
