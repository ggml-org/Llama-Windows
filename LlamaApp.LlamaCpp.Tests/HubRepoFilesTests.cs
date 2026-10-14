using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the Hub repo tree listing that feeds draft-head detection
/// (issue #97): the <c>GET /api/models/{repo}/tree/main</c> payload carries
/// files, directories and (on LFS repos) pointers — only real files matter,
/// and a malformed or empty payload must degrade to an empty list rather
/// than fault the caller.
/// </summary>
public sealed class HubRepoFilesTests
{
    [Fact]
    public void ParseRepoFiles_KeepsOnlyFiles()
    {
        var json = """
            [
              { "type": "file", "path": "Qwen3.8-27B-Q4_0.gguf", "oid": "abc", "size": 100 },
              { "type": "file", "path": "dflash-Qwen3.8-27B-Q4_0.gguf", "oid": "def", "size": 100 },
              { "type": "directory", "path": "subdir" },
              { "type": "file", "path": "readme.md", "oid": "ghi", "size": 10 }
            ]
            """;

        var files = HubClient.ParseRepoFiles(json);

        Assert.Equal(
            ["Qwen3.8-27B-Q4_0.gguf", "dflash-Qwen3.8-27B-Q4_0.gguf", "readme.md"],
            files);
    }

    [Fact]
    public void ParseRepoFiles_EmptyArray_YieldsEmptyList()
    {
        Assert.Empty(HubClient.ParseRepoFiles("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"unexpected\": \"shape\" }")]
    public void ParseRepoFiles_MalformedPayload_YieldsEmptyList(string json)
    {
        Assert.Empty(HubClient.ParseRepoFiles(json));
    }

    [Fact]
    public async Task ListRepoFiles_KeepsTheRepoSlashLiteral()
    {
        // The Hub rejects owner%2Frepo with HTTP 400 (verified live) — the
        // request URI must carry owner/repo as real path segments.
        HttpRequestMessage? captured = null;
        var handler = new CaptureHandler("[]", r => captured = r);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://huggingface.co") };
        var client = new HubClient(null);

        _ = await client.ListRepoFilesAsync(http, "ggml-org/Qwen3.8-27B-GGUF", revision: "main");

        Assert.NotNull(captured);
        Assert.Equal("https://huggingface.co/api/models/ggml-org/Qwen3.8-27B-GGUF/tree/main",
            captured!.RequestUri!.ToString());
    }

    private sealed class CaptureHandler(string body, Action<HttpRequestMessage> capture)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    [Fact]
    public void ParseRepoFiles_DetectsTheSidecarEndToEnd()
    {
        // The full chain the feature depends on: a tree listing → the sidecar
        // classification → the spec-type the INI carries.
        var json = """
            [
              { "type": "file", "path": "Qwen3.8-27B-Q8_0.gguf", "oid": "a", "size": 1 },
              { "type": "file", "path": "mtp-Qwen3.8-27B-Q8_0.gguf", "oid": "b", "size": 1 },
              { "type": "file", "path": "dflash-Qwen3.8-27B-Q8_0.gguf", "oid": "c", "size": 1 }
            ]
            """;

        var files = HubClient.ParseRepoFiles(json);
        var kind = Common.DraftHeads.DetectSidecar(files);

        Assert.Equal(Common.DraftHeadKind.Dflash, kind);
        Assert.Equal("draft-dflash", Common.DraftHeads.SpecType(kind!.Value));
    }
}
