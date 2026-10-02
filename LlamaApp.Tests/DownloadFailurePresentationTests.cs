using LlamaApp.Common;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="DownloadFailurePresentation"/> — the classification
/// boundary (what counts as a failure at all; cancellation never does) and the
/// failure-toast body (headline + guidance, raw detail preserved for the
/// Unknown fallback with the existing 140-char truncation).
/// </summary>
public class DownloadFailurePresentationTests
{
    private static ModelDownloadProgress Failed(string? message, int? status = null, string? exceptionType = null)
        => new("a/b", 0, 0, Done: false, Failed: true,
            Message: message, HttpStatus: status, ExceptionType: exceptionType);

    // ----- Boundary: cancellation is not a failure -------------------------

    [Fact]
    public void Cancellation_Report_Is_Not_A_Failure()
    {
        // The 'Cancelled' report is Failed:false — it must never present as a
        // failure (no headline, no guidance, no toast body).
        var report = new ModelDownloadProgress(
            "a/b", 0, 0, Done: false, Failed: false, Message: "Cancelled");

        Assert.Null(DownloadFailurePresentation.ClassifyReport(report));
    }

    [Fact]
    public void Completion_Report_Is_Not_A_Failure()
    {
        var report = new ModelDownloadProgress(
            "a/b", 0, 0, Done: true, Failed: false, Message: "Download complete");

        Assert.Null(DownloadFailurePresentation.ClassifyReport(report));
    }

    // ----- Classification from a report ------------------------------------

    [Fact]
    public void Failed_Report_Classifies_With_Headline_And_Guidance()
    {
        var failure = DownloadFailurePresentation.ClassifyReport(Failed("Server is not running"));

        Assert.NotNull(failure);
        Assert.Equal(DownloadFailureKind.ServerNotRunning, failure!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
        Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
    }

    [Fact]
    public void Http_Status_On_The_Report_Drives_Classification()
    {
        var failure = DownloadFailurePresentation.ClassifyReport(
            Failed("Server rejected the request (403): nope", status: 403));

        Assert.Equal(DownloadFailureKind.Gated, failure!.Kind);
    }

    [Fact]
    public void Degraded_Master_Wire_Report_Still_Explains_Itself()
    {
        // llama.cpp master sends data:{} → Message falls back to the literal
        // "Download failed" and carries no status/exception. The row/toast must
        // still get a non-empty explanation rather than an empty body.
        var failure = DownloadFailurePresentation.ClassifyReport(Failed("Download failed"));

        Assert.NotNull(failure);
        Assert.Equal(DownloadFailureKind.Unknown, failure!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
        Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
    }

    // ----- Toast body ------------------------------------------------------

    [Fact]
    public void ToastBody_Known_Kind_Shows_Headline_And_Guidance_Without_Raw_Detail()
    {
        var failure = DownloadFailureClassifier.Classify(null, "Server is not running", null);

        var body = DownloadFailurePresentation.ToastBody("Gemma 3", failure);

        Assert.StartsWith("Gemma 3 couldn't be downloaded.", body);
        Assert.Contains(failure.Headline, body);
        Assert.Contains(failure.Guidance, body);
        Assert.DoesNotContain("Server said:", body);
    }

    [Fact]
    public void ToastBody_Unknown_Kind_Appends_The_Raw_Detail()
    {
        var failure = DownloadFailureClassifier.Classify(500, "weird server text", null);

        var body = DownloadFailurePresentation.ToastBody("Gemma 3", failure);

        Assert.Contains("Server said: weird server text.", body);
    }

    [Fact]
    public void ToastBody_Truncates_Long_Raw_Detail_To_140_Chars()
    {
        var detail = new string('x', 200);
        var failure = DownloadFailureClassifier.Classify(500, detail, null);

        var body = DownloadFailurePresentation.ToastBody("Gemma 3", failure);

        Assert.Contains("Server said: " + new string('x', 140) + "….", body);
        Assert.DoesNotContain(new string('x', 141), body);
    }

    [Fact]
    public void ToastBody_Unknown_With_No_Detail_Adds_No_Server_Said()
    {
        var failure = DownloadFailureClassifier.Classify(null, null, null);

        var body = DownloadFailurePresentation.ToastBody("Gemma 3", failure);

        Assert.DoesNotContain("Server said:", body);
    }

    [Fact]
    public void ToastBody_Null_Failure_Falls_Back_To_A_Plain_Sentence()
        => Assert.Equal("Gemma 3 couldn't be downloaded.",
            DownloadFailurePresentation.ToastBody("Gemma 3", null));
}
