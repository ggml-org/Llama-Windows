using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the custom serve-arguments policy. llama.cpp honors the last
/// occurrence of a repeated flag and the custom tokens are appended after the
/// app's own, so a pasted <c>--host 0.0.0.0</c> would silently re-bind the
/// server while the Settings UI still claims localhost. The policy reserves the
/// flags the app owns and redacts secret values for the launch log.
/// </summary>
public sealed class ServeArgumentPolicyTests
{
    private static IReadOnlyList<string> Tokens(params string[] tokens) => tokens;

    // ---- Validation / reserved flags ----

    [Fact]
    public void OrdinaryArguments_Pass()
    {
        Assert.Null(ServeArgumentPolicy.Validate(Tokens("--threads", "4", "--flash-attn")));
        Assert.Null(ServeArgumentPolicy.Validate([]));
    }

    [Theory]
    [InlineData("--host")]
    [InlineData("--port")]
    [InlineData("--api-key")]
    [InlineData("--api-key-file")]
    [InlineData("--models-preset")]
    [InlineData("--ssl")]
    [InlineData("--ssl-key-file")]
    [InlineData("--ssl-cert-file")]
    public void ReservedFlag_IsRejected(string flag)
    {
        var error = ServeArgumentPolicy.Validate(Tokens("--threads", "4", flag, "value"));

        Assert.NotNull(error);
        Assert.Contains(flag, error);
    }

    [Theory]
    [InlineData("--HOST")]
    [InlineData("--Port=1234")]
    [InlineData("--api-key=secret")]
    [InlineData("--API-KEY")]
    public void ReservedFlag_Matched_CaseInsensitively_AndInEqualsForm(string flag)
    {
        Assert.NotNull(ServeArgumentPolicy.Validate(Tokens(flag)));
    }

    [Fact]
    public void ReservedFlag_IsReported_EvenWithoutAValue()
    {
        // "--host" with nothing after it still can't be allowed — the server
        // would read the NEXT custom token (or none) as its value.
        Assert.NotNull(ServeArgumentPolicy.Validate(Tokens("--host")));
    }

    [Theory]
    [InlineData("--threads")]
    [InlineData("--hostile")]          // prefix, not the reserved flag
    [InlineData("--ports")]
    [InlineData("my--host")]
    [InlineData("--api-keys")]
    [InlineData("--hostfile")]
    public void LookalikeTokens_AreNotRejected(string token)
    {
        Assert.Null(ServeArgumentPolicy.Validate(Tokens(token, "value")));
    }

    [Fact]
    public void Validate_ReportsTheFirstReservedFlag()
    {
        var error = ServeArgumentPolicy.Validate(Tokens("--port", "1", "--host", "0.0.0.0"));

        Assert.NotNull(error);
        Assert.Contains("--port", error);
    }

    // ---- Log redaction ----

    [Fact]
    public void RedactForLog_MasksSeparateValue()
    {
        Assert.Equal(
            "--api-key *** --threads 4",
            ServeArgumentPolicy.RedactForLog(Tokens("--api-key", "s3cret", "--threads", "4")));
    }

    [Fact]
    public void RedactForLog_MasksEqualsForm()
    {
        Assert.Equal(
            "--api-key=*** --threads 4",
            ServeArgumentPolicy.RedactForLog(Tokens("--api-key=s3cret", "--threads", "4")));
    }

    [Fact]
    public void RedactForLog_MasksAllKnownSecretFlags()
    {
        var redacted = ServeArgumentPolicy.RedactForLog(Tokens(
            "--api-key", "a", "--api-key-file", "b", "--hf-token", "c",
            "--ssl-key-file", "d", "--ssl-key-pass", "e"));

        Assert.Equal(
            "--api-key *** --api-key-file *** --hf-token *** --ssl-key-file *** --ssl-key-pass ***",
            redacted);
    }

    [Fact]
    public void RedactForLog_LeavesOrdinaryTokensAlone()
    {
        Assert.Equal(
            "--threads 4 --flash-attn",
            ServeArgumentPolicy.RedactForLog(Tokens("--threads", "4", "--flash-attn")));
    }

    [Fact]
    public void RedactForLog_TrailingSecretFlagWithoutValue_IsNotCorrupted()
    {
        Assert.Equal("--api-key", ServeArgumentPolicy.RedactForLog(Tokens("--api-key")));
    }
}
