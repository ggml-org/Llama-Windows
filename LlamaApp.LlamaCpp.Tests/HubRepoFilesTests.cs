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

    // ---- ?blobs=true file sizes (the llama://install quant selection) ----

    [Fact]
    public void ParseRepoFileSizes_ReadsSiblingSizes()
    {
        var json = """
            {
              "id": "ggml-org/repo",
              "siblings": [
                { "rfilename": "model-Q4_K_M.gguf", "size": 123 },
                { "rfilename": "model-Q8_0.gguf" },
                { "rfilename": "readme.md", "size": null },
                { "rfilename": "" }
              ]
            }
            """;

        var files = HubClient.ParseRepoFileSizes(json);

        Assert.Equal(3, files.Count);
        Assert.Equal(new HubClient.RepoFile("model-Q4_K_M.gguf", 123), files[0]);
        Assert.Equal(new HubClient.RepoFile("model-Q8_0.gguf", 0), files[1]); // missing size = unknown
        Assert.Equal(new HubClient.RepoFile("readme.md", 0), files[2]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{ \"unexpected\": \"shape\" }")]
    public void ParseRepoFileSizes_MalformedPayload_YieldsEmptyList(string json)
    {
        Assert.Empty(HubClient.ParseRepoFileSizes(json));
    }

    [Fact]
    public async Task ListRepoFileSizes_KeepsTheRepoSlashLiteral()
    {
        // Same contract as the tree listing: owner/repo stays real path
        // segments (the Hub 400-rejects owner%2Frepo).
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new CaptureHandler("{ \"siblings\": [] }", r => captured = r));
        var client = new HubClient(null);

        _ = await client.ListRepoFileSizesAsync(http, "ggml-org/Qwen3.8-27B-GGUF");

        Assert.NotNull(captured);
        Assert.Equal("https://huggingface.co/api/models/ggml-org/Qwen3.8-27B-GGUF?blobs=true",
            captured!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(404)]  // no such repo
    [InlineData(403)]  // gated
    [InlineData(401)]  // bad token
    public async Task ListRepoFileSizes_SurfacesRejections_AsTypedStatuses(int status)
    {
        // Unlike ListRepoFilesAsync (best-effort), the install flow must tell
        // "repo not found" apart from "gated" — so the status rides out on a
        // HubSearchException.
        using var http = new HttpClient(new StatusHandler(status));
        var client = new HubClient(null);

        var ex = await Assert.ThrowsAsync<HubSearchException>(
            () => client.ListRepoFileSizesAsync(http, "org/name"));

        Assert.Equal(status, ex.Status);
    }

    private sealed class StatusHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(""),
            });
    }
}
