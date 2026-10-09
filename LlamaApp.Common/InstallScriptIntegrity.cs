using System.Security.Cryptography;

namespace LlamaApp.Common;

/// <summary>Why an <c>install.ps1</c> response may be refused.</summary>
public enum InstallScriptVerdict
{
    /// <summary>The response is acceptable.</summary>
    Ok,

    /// <summary>A non-200 response (the server rejected the request).</summary>
    HttpError,

    /// <summary>A 3xx response — redirects are not followed, so the script never runs.</summary>
    Redirected,

    /// <summary>The response did not come from the pinned HTTPS URL/host/path.</summary>
    WrongUri,

    /// <summary>The advertised (or actual) body exceeds <see cref="InstallScriptIntegrity.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>The script hash is not pinned, so provenance can't be verified.</summary>
    Unpinned,

    /// <summary>The script hash did not match the pinned value.</summary>
    HashMismatch,
}

/// <summary>
/// Provenance policy for the downloaded <c>install.ps1</c>. The app runs that
/// script with <c>-ExecutionPolicy Bypass</c>, so a compromised endpoint,
/// DNS answer, or system-trusted TLS-inspection proxy would otherwise be
/// arbitrary code execution. This type centralizes the checks in one pure,
/// unit-testable place:
///
/// <list type="bullet">
/// <item>the response must be a direct <c>200</c> from exactly
/// <c>https://llama.app/install.ps1</c> — redirects are surfaced, not
/// followed, so a 30x to another host is refused;</item>
/// <item>the body is bounded to <see cref="MaxBytes"/> and its SHA-256 is
/// recorded for audit;</item>
/// <item><b>unattended</b> installs (the weekly runtime update) require
/// <see cref="PinnedSha256"/> to be set; an unpinned build can still perform
/// the first (user-initiated, on-launch) install.</item>
/// </list>
///
/// <para>Setting <see cref="PinnedSha256"/> to the current script's hash each
/// release is what re-enables automatic updates; while it is null the weekly
/// path logs and skips rather than executing unverified remote code.</para>
/// </summary>
public static class InstallScriptIntegrity
{
    /// <summary>The one URL the installer may be fetched from.</summary>
    public const string ExpectedScriptUrl = "https://llama.app/install.ps1";

    /// <summary>Expected host (case-insensitive).</summary>
    public const string ExpectedHost = "llama.app";

    /// <summary>Expected absolute path.</summary>
    public const string ExpectedPath = "/install.ps1";

    /// <summary>
    /// Hard cap on the downloaded script. The real file is a few KB; anything
    /// larger is either an attack or an endpoint error.
    /// </summary>
    public const int MaxBytes = 1 * 1024 * 1024;

    /// <summary>
    /// Lower-case hex SHA-256 the script must match for <b>unattended</b>
    /// installs. Null disables unattended installs (they are skipped rather
    /// than run unverified). Update this when pinning a new script revision.
    /// </summary>
    public const string? PinnedSha256 = null;

    /// <summary>True when the verdict allows the script to run.</summary>
    public static bool IsOk(InstallScriptVerdict verdict) => verdict == InstallScriptVerdict.Ok;

    /// <summary>
    /// Validates a response line for the installer fetch. <paramref name="finalUri"/>
    /// is the URI the response actually came from (equal to the request URI when
    /// redirects are disabled).
    /// </summary>
    public static InstallScriptVerdict ValidateResponse(int statusCode, Uri? finalUri, long? contentLength)
    {
        if (statusCode is >= 300 and < 400) return InstallScriptVerdict.Redirected;
        if (statusCode != 200) return InstallScriptVerdict.HttpError;
        if (finalUri is null || !IsExpectedUri(finalUri)) return InstallScriptVerdict.WrongUri;
        if (contentLength is { } len && (len <= 0 || len > MaxBytes)) return InstallScriptVerdict.TooLarge;
        return InstallScriptVerdict.Ok;
    }

    /// <summary>
    /// True only for HTTPS on <see cref="ExpectedHost"/> (default port) with
    /// exactly <see cref="ExpectedPath"/>.
    /// </summary>
    public static bool IsExpectedUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort) return false;
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        return string.Equals(uri.AbsolutePath, ExpectedPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares the downloaded script's hash against the pin.
    /// <see cref="InstallScriptVerdict.Unpinned"/> means no pin is configured
    /// (the caller decides whether that is acceptable); <see cref="InstallScriptVerdict.HashMismatch"/>
    /// is always fatal.
    /// </summary>
    public static InstallScriptVerdict ValidateHash(string? sha256Hex, string? pinned = PinnedSha256)
    {
        if (string.IsNullOrWhiteSpace(pinned)) return InstallScriptVerdict.Unpinned;
        if (string.IsNullOrWhiteSpace(sha256Hex)) return InstallScriptVerdict.HashMismatch;
        return string.Equals(sha256Hex.Trim(), pinned.Trim(), StringComparison.OrdinalIgnoreCase)
            ? InstallScriptVerdict.Ok
            : InstallScriptVerdict.HashMismatch;
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="data"/>.</summary>
    public static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Reads <paramref name="stream"/> into memory, refusing to buffer more
    /// than <paramref name="maxBytes"/>. Keeps a hostile/oversized response
    /// from exhausting memory before it is ever executed.
    /// </summary>
    public static async Task<byte[]> ReadBoundedAsync(
        Stream stream, int maxBytes = MaxBytes, CancellationToken cancel = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"install.ps1 exceeds the {maxBytes}-byte limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Human-readable reason for refusing a script.</summary>
    public static string Describe(InstallScriptVerdict verdict) => verdict switch
    {
        InstallScriptVerdict.HttpError => "the server returned a non-200 response",
        InstallScriptVerdict.Redirected => "the server redirected the download (redirects are refused)",
        InstallScriptVerdict.WrongUri => $"the response did not come from {ExpectedScriptUrl}",
        InstallScriptVerdict.TooLarge => $"the script exceeds the {MaxBytes}-byte limit",
        InstallScriptVerdict.Unpinned => "the script hash is not pinned and this is an unattended install",
        InstallScriptVerdict.HashMismatch => "the script hash did not match the pinned value",
        _ => "the response was accepted",
    };
}
