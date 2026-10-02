using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for <see cref="DownloadFailureClassifier"/>: the pure mapping from
/// download-failure inputs (HTTP status, error text, exception type) to an
/// actionable category. The headline/guidance wording itself is free to change;
/// the contract is the category, the preserved verbatim raw detail, and that no
/// real failure (and no malformed input) leaves the user without an explanation.
/// </summary>
public class DownloadFailureClassifierTests
{
    // ----- Status-code mapping ---------------------------------------------

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Auth_Status_Maps_To_Gated(int status)
        => Assert.Equal(DownloadFailureKind.Gated,
            DownloadFailureClassifier.Classify(status, null, null).Kind);

    [Fact]
    public void NotFound_Status_Maps_To_NotFound()
        => Assert.Equal(DownloadFailureKind.NotFound,
            DownloadFailureClassifier.Classify(404, null, null).Kind);

    [Fact]
    public void Other_4xx_Status_Maps_To_RequestRejected()
        => Assert.Equal(DownloadFailureKind.RequestRejected,
            DownloadFailureClassifier.Classify(400, null, null).Kind);

    [Fact]
    public void Patternless_5xx_Status_Maps_To_Unknown()
    {
        // llama.cpp maps any non-invalid_argument download exception to a bare
        // 500 — a server-internal fault. Without explanatory text the honest
        // answer is "unknown, check the log", not "the server rejected it".
        Assert.Equal(DownloadFailureKind.Unknown,
            DownloadFailureClassifier.Classify(500, null, null).Kind);
    }

    [Fact]
    public void No_Input_At_All_Maps_To_Unknown_With_No_Raw_Detail()
    {
        var failure = DownloadFailureClassifier.Classify(null, null, null);

        Assert.Equal(DownloadFailureKind.Unknown, failure.Kind);
        Assert.Null(failure.RawDetail);
        Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
        Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
    }

    // ----- Precedence: the server's 500 wrapper around HF's real status -----

    [Fact]
    public void Gated_Text_Wins_Over_A_5xx_Wrapper()
    {
        // The common wire shape: llama.cpp's ex_wrapper turns the HF fetch
        // failure ("GET failed (401): …") into an HTTP 500. The text is the
        // authoritative signal.
        var failure = DownloadFailureClassifier.Classify(
            500, "GET failed (401): {\"error\":\"Repository Gated\"}", null);

        Assert.Equal(DownloadFailureKind.Gated, failure.Kind);
        Assert.Equal("GET failed (401): {\"error\":\"Repository Gated\"}", failure.RawDetail);
    }

    [Fact]
    public void NotFound_Text_Wins_Over_A_5xx_Wrapper()
        => Assert.Equal(DownloadFailureKind.NotFound,
            DownloadFailureClassifier.Classify(500, "GET failed (404): not found", null).Kind);

    // ----- Message-pattern mapping -----------------------------------------

    [Theory]
    [InlineData("no space left on device")]
    [InlineData("Error writing file: not enough space")]
    [InlineData("ENOSPC: failed to write")]
    [InlineData("Disk full while writing")]
    public void DiskFull_Wording_Maps_To_DiskFull(string detail)
        => Assert.Equal(DownloadFailureKind.DiskFull,
            DownloadFailureClassifier.Classify(null, detail, null).Kind);

    [Theory]
    [InlineData("HTTPLIB failed: Connection reset by peer")]
    [InlineData("error: cannot make GET request")]
    [InlineData("The response ended prematurely")]
    [InlineData("An error occurred while sending the request")]
    [InlineData("Operation aborted")]
    public void Connection_Wording_Maps_To_ConnectionLost(string detail)
        => Assert.Equal(DownloadFailureKind.ConnectionLost,
            DownloadFailureClassifier.Classify(null, detail, null).Kind);

    [Theory]
    [InlineData("GET failed (401): invalid token")]
    [InlineData("401 Unauthorized")]
    [InlineData("403 Forbidden")]
    [InlineData("Repository is Gated")]
    [InlineData("You are not logged in")]
    [InlineData("Download 'https://huggingface.co/x/y' failed with status code: 401")]
    public void Gated_Wording_Maps_To_Gated(string detail)
        => Assert.Equal(DownloadFailureKind.Gated,
            DownloadFailureClassifier.Classify(null, detail, null).Kind);

    [Theory]
    [InlineData("Download 'https://huggingface.co/x/y' failed with status code: 404")]
    [InlineData("Repository not found")]
    [InlineData("model does not exist")]
    [InlineData("GET failed (404): nope")]
    public void NotFound_Wording_Maps_To_NotFound(string detail)
        => Assert.Equal(DownloadFailureKind.NotFound,
            DownloadFailureClassifier.Classify(null, detail, null).Kind);

    [Fact]
    public void Server_Not_Running_Wording_Maps_To_ServerNotRunning()
        => Assert.Equal(DownloadFailureKind.ServerNotRunning,
            DownloadFailureClassifier.Classify(null, "Server is not running", null).Kind);

    // ----- Exception-type input --------------------------------------------

    [Fact]
    public void SocketException_Alone_Maps_To_ConnectionLost()
        => Assert.Equal(DownloadFailureKind.ConnectionLost,
            DownloadFailureClassifier.Classify(null, null, "SocketException").Kind);

    [Fact]
    public void HttpRequestException_Alone_Maps_To_ConnectionLost()
        => Assert.Equal(DownloadFailureKind.ConnectionLost,
            DownloadFailureClassifier.Classify(null, null, "HttpRequestException").Kind);

    [Fact]
    public void Exception_Type_Is_Case_Insensitive()
        => Assert.Equal(DownloadFailureKind.ConnectionLost,
            DownloadFailureClassifier.Classify(null, null, "socketexception").Kind);

    // ----- Unknown fallback preserves the raw detail -----------------------

    [Fact]
    public void Unknown_Fallback_Carries_The_Raw_Detail_Verbatim()
    {
        const string detail = "some totally unmapped server text: 0x8000_1234";

        var failure = DownloadFailureClassifier.Classify(500, detail, "InvalidOperationException");

        Assert.Equal(DownloadFailureKind.Unknown, failure.Kind);
        Assert.Equal(detail, failure.RawDetail);
        Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
        Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
    }

    // ----- Every kind explains itself --------------------------------------

    public static TheoryData<DownloadFailureKind> RealKinds => new()
    {
        DownloadFailureKind.Gated,
        DownloadFailureKind.NotFound,
        DownloadFailureKind.DiskFull,
        DownloadFailureKind.ConnectionLost,
        DownloadFailureKind.ServerNotRunning,
        DownloadFailureKind.RequestRejected,
        DownloadFailureKind.Unknown,
    };

    [Theory]
    [MemberData(nameof(RealKinds))]
    public void Classification_Onto_A_Kind_Always_Produces_Headline_And_Guidance(DownloadFailureKind expected)
    {
        // One representative input per kind; each must carry a non-empty
        // headline + guidance (no outcome leaves the user without a reason).
        var failure = expected switch
        {
            DownloadFailureKind.Gated => DownloadFailureClassifier.Classify(401, null, null),
            DownloadFailureKind.NotFound => DownloadFailureClassifier.Classify(404, null, null),
            DownloadFailureKind.DiskFull => DownloadFailureClassifier.Classify(null, "no space left on device", null),
            DownloadFailureKind.ConnectionLost => DownloadFailureClassifier.Classify(null, null, "SocketException"),
            DownloadFailureKind.ServerNotRunning => DownloadFailureClassifier.Classify(null, "Server is not running", null),
            DownloadFailureKind.RequestRejected => DownloadFailureClassifier.Classify(400, null, null),
            _ => DownloadFailureClassifier.Classify(null, null, null),
        };

        Assert.Equal(expected, failure.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
        Assert.False(string.IsNullOrWhiteSpace(failure.Guidance));
    }

    // ----- Totality (a classification problem must never throw) ------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Blank_Or_Null_Detail_Never_Throws(string? detail)
    {
        var failure = DownloadFailureClassifier.Classify(null, detail, null);

        Assert.Null(failure.RawDetail);
    }

    [Fact]
    public void Very_Large_Detail_Never_Throws_And_Is_Preserved_Verbatim()
    {
        var detail = new string('x', 500_000);

        var failure = DownloadFailureClassifier.Classify(500, detail, null);

        Assert.Equal(detail, failure.RawDetail);
    }

    [Fact]
    public void Detail_Is_Preserved_Verbatim_With_No_Trimming()
    {
        const string detail = "  GET failed (401): token  ";

        var failure = DownloadFailureClassifier.Classify(null, detail, null);

        Assert.Equal(detail, failure.RawDetail);
    }
}
