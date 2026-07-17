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
/// <param name="Message">Human-readable status / error message, when relevant.</param>
public sealed record ModelDownloadProgress(
    string Model,
    long DownloadedBytes,
    long TotalBytes,
    bool Done,
    bool Failed,
    string? Message = null)
{
    /// <summary>Fraction completed (0..1), or 0 when the total isn't known yet.</summary>
    public double Fraction => TotalBytes > 0.0d ? (double)DownloadedBytes / TotalBytes : 0.0d;
}