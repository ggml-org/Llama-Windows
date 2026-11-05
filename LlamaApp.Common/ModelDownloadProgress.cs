using System.Text.Json;

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
/// <param name="FractionOverride">A stateful aggregate fraction, when supplied
/// by <see cref="ModelDownloadProgressAccumulator"/>. Raw byte counts remain
/// available for speed/ETA accounting; the override only prevents the displayed
/// fraction moving backward when another file is discovered mid-download.</param>
public sealed record ModelDownloadProgress(
    string Model,
    long DownloadedBytes,
    long TotalBytes,
    bool Done,
    bool Failed,
    string? Message = null,
    int? HttpStatus = null,
    string? ExceptionType = null,
    double? FractionOverride = null)
{
    /// <summary>Fraction completed (0..1), or 0 when the total isn't known yet.</summary>
    public double Fraction => FractionOverride is { } fraction
        ? Math.Clamp(fraction, 0, 1)
        : TotalBytes > 0
            ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1)
            : 0.0d;
}

/// <summary>
/// Accumulates one download attempt's per-URL SSE events. llama.cpp may emit a
/// complete progress map on one event and only the active file on the next;
/// treating each event as a fresh complete map drops every omitted file and
/// makes a multi-GB download appear to restart. This accumulator retains those
/// files, keeps per-file byte counts/totals stable, and carries a high-water
/// fraction for late-discovered sidecars.
/// </summary>
/// <remarks>
/// Create a new instance for every download or watch attempt. A deliberate
/// retry therefore starts at zero rather than being hidden by the previous
/// attempt's high-water mark. Terminal failure/cancellation reports bypass the
/// accumulator and are never converted into success or completion.
/// Per-file counts are also high-water marks within the attempt: transient SSE
/// snapshots cannot move the UI backward. A server-side abort starts a new
/// attempt and a new accumulator, so real user-visible retries still reset.
/// </remarks>
public sealed class ModelDownloadProgressAccumulator
{
    private readonly Dictionary<string, (long Done, long Total)> _files =
        new(StringComparer.Ordinal);
    private double _fraction;

    /// <summary>Merges one <c>download_progress</c> data payload.</summary>
    public ModelDownloadProgress Observe(string model, JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("progress", out var progress) &&
            progress.ValueKind == JsonValueKind.Object)
        {
            foreach (var url in progress.EnumerateObject())
            {
                if (url.Value.ValueKind != JsonValueKind.Object) continue;

                _files.TryGetValue(url.Name, out var previous);
                var done = previous.Done;
                var total = previous.Total;

                if (ReadNonNegativeInt64(url.Value, "done", out var reportedDone))
                    done = Math.Max(done, reportedDone);
                if (ReadNonNegativeInt64(url.Value, "total", out var reportedTotal))
                    total = Math.Max(total, reportedTotal);
                if (total > 0) done = Math.Min(done, total);

                _files[url.Name] = (done, total);
            }
        }

        long downloaded = 0, totalBytes = 0;
        foreach (var file in _files.Values)
        {
            downloaded = SaturatingAdd(downloaded, file.Done);
            totalBytes = SaturatingAdd(totalBytes, file.Total);
        }

        if (totalBytes > 0)
            _fraction = Math.Max(_fraction, Math.Min(1, (double)downloaded / totalBytes));

        return new ModelDownloadProgress(
            model, downloaded, totalBytes, Done: false, Failed: false,
            FractionOverride: totalBytes > 0 ? _fraction : null);
    }

    private static bool ReadNonNegativeInt64(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out result) && result >= 0;
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
