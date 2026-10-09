namespace LlamaApp.Common;

/// <summary>
/// Navigation policy for the overlay's embedded WebView2. The overlay is a
/// chromeless window with no address bar, so a page that navigates (or is
/// redirected) off the local llama server would be rendered with the app's
/// visual authority and the user could not tell. Only the running server's own
/// origin may display inside the overlay; anything else is cancelled, and new
/// windows (target=_blank) are handed to the system browser — and only for
/// plain http(s).
/// </summary>
public static class WebOriginPolicy
{
    /// <summary>
    /// True when <paramref name="uri"/> is a document on the local llama
    /// server: plain http, the exact connect host, and the exact port.
    /// </summary>
    public static bool IsServerOrigin(Uri? uri, string host, int port)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Port == port;
    }

    /// <summary>
    /// True when <paramref name="uri"/> may be handed to the system browser
    /// from a new-window request: absolute http(s) only — anything else
    /// (file:, ms-appx:, custom schemes) is dropped.
    /// </summary>
    public static bool IsOpenableExternal(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        return uri.Scheme is "http" or "https";
    }
}
