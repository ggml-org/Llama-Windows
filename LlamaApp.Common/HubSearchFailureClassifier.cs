namespace LlamaApp.Common;

/// <summary>
/// The actionable bucket a Hub search failure falls into. Drives the
/// user-facing headline/guidance shown on the search status line.
/// </summary>
public enum HubSearchFailureKind
{
    /// <summary>The Hub rate-limited the search (HTTP 429). Without a token
    /// anonymous searches are throttled; a configured token lifts the limits.</summary>
    RateLimited,

    /// <summary>The configured token was rejected (HTTP 401/403) — only
    /// meaningful when a token IS configured.</summary>
    InvalidToken,

    /// <summary>The Hub could not be reached (connection/timeout) — the
    /// original transport failure.</summary>
    Network,

    /// <summary>Anything else — the app log remains the fallback.</summary>
    Unknown,
}

/// <summary>
/// A classified Hub search failure: a short user-facing <paramref name="Headline"/>
/// and actionable <paramref name="Guidance"/>. Composing the two with a single
/// space reconstitutes the full status-line sentence (the frozen wording for
/// <see cref="HubSearchFailureKind.Network"/> and
/// <see cref="HubSearchFailureKind.Unknown"/>).
/// </summary>
public sealed record HubSearchFailure(HubSearchFailureKind Kind, string Headline, string Guidance);

/// <summary>
/// Pure, total classifier mapping a Hub search failure's raw inputs — the
/// numeric HTTP status of a rejected <c>GET /api/models</c>, the runtime type
/// name of a caught exception, and whether a token is configured — into an
/// actionable <see cref="HubSearchFailure"/>.
///
/// <para>Kept dependency-free and side-effect-free so it is unit-testable and
/// can never throw into the search path (a classification problem must never
/// mask the original failure): every input is optional, the token flag is a
/// plain input (this assembly never reads Settings), and the whole body is
/// guarded so even malformed input yields an
/// <see cref="HubSearchFailureKind.Unknown"/> result.</para>
/// </summary>
public static class HubSearchFailureClassifier
{
    /// <summary>
    /// Classifies a Hub search failure. All inputs are optional; never throws.
    /// </summary>
    /// <param name="httpStatus">HTTP status of a non-success search response,
    /// when one was observed (e.g. from a typed <c>HubSearchException</c>).</param>
    /// <param name="exceptionType">The runtime type name of a caught exception
    /// (e.g. <c>HttpRequestException</c>, <c>TaskCanceledException</c>), when
    /// the failure surfaced as an exception.</param>
    /// <param name="tokenConfigured">Whether a Hugging Face token is configured
    /// in Settings. Only affects the guidance for auth failures.</param>
    public static HubSearchFailure Classify(int? httpStatus, string? exceptionType, bool tokenConfigured)
    {
        try
        {
            // A status is the strongest signal and is checked BEFORE the
            // exception type: a typed HubSearchException is itself an
            // HttpRequestException, but its status is far more specific.
            if (httpStatus == 429)
                return Create(HubSearchFailureKind.RateLimited);

            if (httpStatus is 401 or 403)
            {
                // Token guidance only makes sense when a token is configured;
                // an anonymous 401/403 is anomalous → the generic fallback.
                return tokenConfigured
                    ? Create(HubSearchFailureKind.InvalidToken)
                    : Create(HubSearchFailureKind.Unknown);
            }

            // Any other parseable status (e.g. 500) is a server-side failure
            // we can't act on specifically.
            if (httpStatus is not null)
                return Create(HubSearchFailureKind.Unknown);

            if (IsException(exceptionType, "HttpRequestException") ||
                IsException(exceptionType, "TaskCanceledException") ||
                IsException(exceptionType, "TimeoutException"))
                return Create(HubSearchFailureKind.Network);

            return Create(HubSearchFailureKind.Unknown);
        }
        catch
        {
            // Never let a classification problem mask the original failure.
            return new HubSearchFailure(
                HubSearchFailureKind.Unknown, "Search failed.", "Try again.");
        }
    }

    private static bool IsException(string? exceptionType, string name)
        => string.Equals(exceptionType, name, StringComparison.OrdinalIgnoreCase);

    private static HubSearchFailure Create(HubSearchFailureKind kind) => kind switch
    {
        HubSearchFailureKind.RateLimited => new HubSearchFailure(kind,
            "Searches are rate-limited without an account.",
            "Add a Hugging Face token in Settings to lift the limits."),
        HubSearchFailureKind.InvalidToken => new HubSearchFailure(kind,
            "The configured Hugging Face token looks invalid.",
            "Update or remove it in Settings."),
        // Headline + " " + Guidance must reconstitute the frozen sentences.
        HubSearchFailureKind.Network => new HubSearchFailure(kind,
            "Couldn't reach Hugging Face.",
            "Check your connection and try again."),
        _ => new HubSearchFailure(kind, "Search failed.", "Try again."),
    };
}
