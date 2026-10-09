using System.Text.Json;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the sample API request strings: they must target the server's
/// connect address (which follows the Settings listen choice) rather than a
/// hard-coded localhost, so a server bound to a specific interface produces a
/// URL that actually reaches it.
/// </summary>
public sealed class ApiRequestPresentationTests
{
    [Fact]
    public void BuildCurlCommand_UsesTheGivenAddress()
    {
        var cmd = ApiRequestPresentation.BuildCurlCommand(
            "192.168.1.42", 9931, "unsloth/Qwen3-4B-GGUF:Q4_K_M");

        Assert.StartsWith("curl ", cmd);
        Assert.Contains("http://192.168.1.42:9931/v1/chat/completions", cmd);
        Assert.Contains("unsloth/Qwen3-4B-GGUF:Q4_K_M", cmd);
    }

    [Fact]
    public void BuildWebUiUrl_UsesTheGivenAddress()
    {
        var url = ApiRequestPresentation.BuildWebUiUrl(
            "10.0.0.5", 8080, "ggml-org/gemma-3-4b-it-GGUF:Q4_K_M");

        Assert.Equal(
            "http://10.0.0.5:8080?model=ggml-org%2Fgemma-3-4b-it-GGUF%3AQ4_K_M",
            url);
    }

    [Fact]
    public void BuildWebUiUrl_EscapesTheModelId()
    {
        var url = ApiRequestPresentation.BuildWebUiUrl(
            "127.0.0.1", 9931, "a/b:Q4_K_M");

        Assert.Equal("http://127.0.0.1:9931?model=a%2Fb%3AQ4_K_M", url);
    }

    // ---- Injection resistance: model ids are remote data ----

    [Fact]
    public void BuildCurlCommand_JsonEscapesTheModelId()
    {
        // A raw-interpolated body would let this append extra JSON fields.
        var id = "evil\",\"x\":1,\"y\":\"";
        var cmd = ApiRequestPresentation.BuildCurlCommand("127.0.0.1", 9931, id);

        var json = ExtractDataPayload(cmd);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(id, doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("x", out _));
        Assert.False(doc.RootElement.TryGetProperty("y", out _));
    }

    [Fact]
    public void BuildCurlCommand_SingleQuotesThePayload()
    {
        var cmd = ApiRequestPresentation.BuildCurlCommand("127.0.0.1", 9931, "a/b:Q4_K_M");

        // POSIX single quotes stop $(...)/backtick expansion on paste.
        Assert.Contains("-d '", cmd);
        Assert.EndsWith("'", cmd);
    }

    /// <summary>Pulls the shell-quoted <c>-d</c> payload back out for parsing.</summary>
    private static string ExtractDataPayload(string cmd)
    {
        const string marker = "-d '";
        var start = cmd.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return cmd[start..cmd.LastIndexOf('\'')];
    }

    // ---- API-key threading (non-loopback binds) ----

    [Fact]
    public void BuildWebUiUrl_NeverCarriesTheApiKey()
    {
        // llama.cpp's WebUI reads its key from a typed dialog (no query form);
        // a ?api_key= would be ignored by the page and leak into browser
        // history, caches and server logs. The key must never be in a URL.
        var url = ApiRequestPresentation.BuildWebUiUrl(
            "192.168.1.42", 9931, "a/b:Q4_K_M");

        Assert.Equal("http://192.168.1.42:9931?model=a%2Fb%3AQ4_K_M", url);
        Assert.DoesNotContain("api_key", url);
    }

    [Fact]
    public void BuildCurlCommand_WithKey_IncludesBearerHeader()
    {
        var cmd = ApiRequestPresentation.BuildCurlCommand(
            "192.168.1.42", 9931, "a/b:Q4_K_M", apiKey: "cafe1234");

        Assert.Contains("-H \"Authorization: Bearer cafe1234\"", cmd);
        // The header sits before the payload so the request is well-formed.
        Assert.True(cmd.IndexOf("Authorization") < cmd.IndexOf("-d "));
    }

    [Fact]
    public void BuildCurlCommand_WithoutKey_HasNoBearerHeader()
    {
        var cmd = ApiRequestPresentation.BuildCurlCommand(
            "127.0.0.1", 9931, "a/b:Q4_K_M", apiKey: null);

        Assert.DoesNotContain("Authorization", cmd);
    }
}
