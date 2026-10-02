namespace LlamaApp.Common;

/// <summary>
/// The actionable bucket a download failure falls into. Drives the user-facing
/// headline/guidance shown on the failed row and the failure toast, while the
/// original technical detail is preserved verbatim for diagnosis.
/// </summary>
public enum DownloadFailureKind
{
    /// <summary>Authentication/authorization problem (HTTP 401/403, or a
    /// "gated"/"forbidden"/"unauthorized" message) — the repo needs a
    /// Hugging Face access token.</summary>
    Gated,

    /// <summary>The model/repo doesn't exist (HTTP 404 or "not found").</summary>
    NotFound,

    /// <summary>The model cache drive ran out of space (ENOSPC / "no space left").</summary>
    DiskFull,

    /// <summary>The connection dropped before the download finished.</summary>
    ConnectionLost,

    /// <summary>The llama.cpp server isn't running.</summary>
    ServerNotRunning,

    /// <summary>The server rejected the request for another reason (other 4xx).</summary>
    RequestRejected,

    /// <summary>Anything else — the raw detail + app log remain the fallback.</summary>
    Unknown,
}

/// <summary>
/// A classified download failure: a short user-facing <paramref name="Headline"/>,
/// actionable <paramref name="Guidance"/>, and the original technical
/// <paramref name="RawDetail"/> (verbatim, or <c>null</c> when none was
/// captured) so the raw error is never discarded.
/// </summary>
public sealed record DownloadFailure(
    DownloadFailureKind Kind,
    string Headline,
    string Guidance,
    string? RawDetail);

/// <summary>
/// Pure, total classifier mapping a download failure's raw inputs — the HTTP
/// status of a rejected POST, the error/message text from the SSE
/// <c>download_failed</c> event (or a server body / exception message), and the
/// exception type — into an actionable <see cref="DownloadFailure"/>.
///
/// <para>Kept dependency-free and side-effect-free so it is unit-testable and
/// can never throw into the download path (a classification problem must never
/// mask the original failure): every input is optional, and the whole body is
/// guarded so even malformed input yields an <see cref="DownloadFailureKind.Unknown"/>
/// result that still carries the raw detail.</para>
/// </summary>
public static class DownloadFailureClassifier
{
    // Order matters within the pattern pass: the most specific/actionable
    // wording wins. Status is handled before patterns (401/403 → Gated,
    // 404 → NotFound), then patterns, then the remaining status buckets.
    private static readonly string[] DiskFullPatterns =
    {
        "no space left",
        "not enough space",
        "insufficient space",
        "disk full",
        "enospc",
    };

    private static readonly string[] ConnectionLostPatterns =
    {
        "connection reset",
        "connection refused",
        "broken pipe",
        "response ended prematurely",
        "error occurred while sending",
        "forcibly closed",
        "httplib failed",
        "cannot make get request",
        "aborted",
    };

    private static readonly string[] GatedPatterns =
    {
        "get failed (401",
        "get failed (403",
        "status code: 401",
        "status code: 403",
        "unauthorized",
        "forbidden",
        "gated",
        "logged in",
    };

    private static readonly string[] NotFoundPatterns =
    {
        "get failed (404",
        "status code: 404",
        "not found",
        "does not exist",
    };

    private static readonly string[] ServerNotRunningPatterns =
    {
        "server is not running",
    };

    /// <summary>
    /// Classifies a download failure. All inputs are optional. Never throws.
    /// </summary>
    /// <param name="httpStatus">HTTP status code of a rejected POST, when one
    /// was observed.</param>
    /// <param name="detail">The raw error text (SSE error field, server body,
    /// exception message) — carried verbatim into the result.</param>
    /// <param name="exceptionType">The runtime type name of a caught exception
    /// (e.g. <c>HttpRequestException</c>, <c>SocketException</c>), when the
    /// failure surfaced as an exception.</param>
    public static DownloadFailure Classify(int? httpStatus, string? detail, string? exceptionType)
    {
        // RawDetail is the original text, unmodified (whitespace-only → null).
        var rawDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;

        try
        {
            // 1-2. A definitive HTTP status from the POST rejection is the
            // strongest signal.
            if (httpStatus is 401 or 403)
                return Create(DownloadFailureKind.Gated, rawDetail);
            if (httpStatus is 404)
                return Create(DownloadFailureKind.NotFound, rawDetail);

            // 3. Message patterns. These run before the remaining status
            // buckets so the common llama.cpp wire shape (a 500 wrapper around
            // a "GET failed (401): …" HF text — the server maps any download
            // exception to 500) still classifies as Gated.
            if (MatchesAny(detail, DiskFullPatterns))
                return Create(DownloadFailureKind.DiskFull, rawDetail);

            if (MatchesAny(detail, ConnectionLostPatterns) ||
                IsException(exceptionType, "SocketException"))
                return Create(DownloadFailureKind.ConnectionLost, rawDetail);

            if (MatchesAny(detail, GatedPatterns))
                return Create(DownloadFailureKind.Gated, rawDetail);

            if (MatchesAny(detail, NotFoundPatterns))
                return Create(DownloadFailureKind.NotFound, rawDetail);

            if (MatchesAny(detail, ServerNotRunningPatterns))
                return Create(DownloadFailureKind.ServerNotRunning, rawDetail);

            // 4. Other client errors → the server rejected the request.
            if (httpStatus is >= 400 and < 500)
                return Create(DownloadFailureKind.RequestRejected, rawDetail);

            // 5. A transport exception with no other clue → connection lost.
            if (IsException(exceptionType, "HttpRequestException"))
                return Create(DownloadFailureKind.ConnectionLost, rawDetail);

            // 6. Fallback — the raw detail + app log carry the diagnosis.
            return Create(DownloadFailureKind.Unknown, rawDetail);
        }
        catch
        {
            // Never let a classification problem mask the original failure.
            return new DownloadFailure(
                DownloadFailureKind.Unknown,
                "The download failed for an unknown reason.",
                "Check the app log for details.",
                detail);
        }
    }

    private static bool MatchesAny(string? detail, string[] patterns)
    {
        if (string.IsNullOrEmpty(detail)) return false;
        foreach (var pattern in patterns)
        {
            if (detail.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsException(string? exceptionType, string name)
        => string.Equals(exceptionType, name, StringComparison.OrdinalIgnoreCase);

    private static DownloadFailure Create(DownloadFailureKind kind, string? rawDetail) => kind switch
    {
        DownloadFailureKind.Gated => new DownloadFailure(kind,
            "This model needs permission to download.",
            "Add your Hugging Face access token in Settings — only needed for private or gated repos.",
            rawDetail),
        DownloadFailureKind.NotFound => new DownloadFailure(kind,
            "The model wasn't found on Hugging Face.",
            "Check the model and quant, or try a different one.",
            rawDetail),
        DownloadFailureKind.DiskFull => new DownloadFailure(kind,
            "Not enough disk space to download the model.",
            "Free up space on the model cache drive and retry.",
            rawDetail),
        DownloadFailureKind.ConnectionLost => new DownloadFailure(kind,
            "The connection dropped during the download.",
            "Check that the llama.cpp server is still running, then retry.",
            rawDetail),
        DownloadFailureKind.ServerNotRunning => new DownloadFailure(kind,
            "The llama.cpp server isn't running.",
            "Start the server and retry the download.",
            rawDetail),
        DownloadFailureKind.RequestRejected => new DownloadFailure(kind,
            "The server rejected the download request.",
            "Check the app log for the server's response, then retry.",
            rawDetail),
        _ => new DownloadFailure(kind,
            "The download failed for an unknown reason.",
            $"Check the app log for details ({SafeLogDirectory()}).",
            rawDetail),
    };

    // LogDirectory is computed (valid before the first write) but guard it
    // anyway — this method must be total.
    private static string SafeLogDirectory()
    {
        try { return Log.LogDirectory; }
        catch { return "%LOCALAPPDATA%\\Llama\\logs"; }
    }
}
