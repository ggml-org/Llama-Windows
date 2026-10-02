namespace LlamaApp.Common;

/// <summary>
/// Progress report for a model download — fed to <c>IProgress&lt;DownloadProgress&gt;</c>
/// by <c>LlamaManager.DownloadModel</c> as the llama server streams SSE updates.
/// </summary>
/// <param name="Model">Repo id of the model being downloaded.</param>
/// <param name="DownloadedBytes">Bytes fetched so far (summed across all files in a multi-file repo).</param>
/// <param name="TotalBytes">Total bytes to fetch; 0 when the server hasn't reported a size yet.</param>
/// <param name="Done">True, once the download has completed successfully.</param>
/// <param name="Failed">True if the download failed.</param>
/// <param name="Message">Human-readable status / error message, when relevant.
/// On a failure this carries the raw technical detail (the SSE
/// <c>download_failed</c> error text, the server's rejection body, or the
/// exception message) verbatim — see <see cref="DownloadFailureClassifier"/>.</param>
/// <param name="HttpStatus">HTTP status of a rejected POST, when one was
/// observed — optional classification input.</param>
/// <param name="ExceptionType">Runtime type name of a failure that surfaced as
/// an exception (e.g. <c>HttpRequestException</c>, <c>SocketException</c>),
/// when relevant — optional classification input.</param>
public sealed record ModelDownloadProgress(
    string Model,
    long DownloadedBytes,
    long TotalBytes,
    bool Done,
    bool Failed,
    string? Message = null,
    int? HttpStatus = null,
    string? ExceptionType = null)
{
    /// <summary>Fraction completed (0..1), or 0 when the total isn't known yet.</summary>
    public double Fraction => TotalBytes > 0.0d ? (double)DownloadedBytes / TotalBytes : 0.0d;
}