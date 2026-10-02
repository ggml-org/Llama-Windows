using System.Net;
using System.Text;
using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="HubClient.ParseModels"/>: the /api/models JSON
/// array → <see cref="HubClient.HubSearchResult"/> mapping — id/downloads/
/// likes extraction, missing-field tolerance, and malformed-JSON resilience.
/// </summary>
public class HubSearchTests
{
    // A realistic /api/models?filter=gguf response — extra per-entry payload
    // (siblings, tags, config, …) must be ignored, only id/downloads/likes mapped.
    private const string ModelsJson = """
        [
          {
            "_id": "65f1a2b3c4d5e6f7a8b9c0d1",
            "id": "ggml-org/gemma-3-4b-it-GGUF",
            "likes": 312,
            "downloads": 1234567,
            "pipeline_tag": "text-generation",
            "library_name": "gguf",
            "tags": [ "gguf", "gemma3" ],
            "siblings": [ { "rfilename": "gemma-3-4b-it-Q4_0.gguf" } ]
          },
          { "id": "unsloth/Qwen3-0.6B-GGUF" },
          { "id": "org/repo", "downloads": 0, "likes": 0 },
          { "downloads": 42 }
        ]
        """;

    [Fact]
    public void Parses_ids_downloads_and_likes()
    {
        var results = HubClient.ParseModels(ModelsJson);

        Assert.Equal(4, results.Count);
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF", results[0].Id);
        Assert.Equal(1234567, results[0].Downloads);
        Assert.Equal(312, results[0].Likes);
    }

    [Fact]
    public void Missing_fields_map_to_zero()
    {
        var results = HubClient.ParseModels(ModelsJson);

        Assert.Equal(0, results[1].Downloads);
        Assert.Equal(0, results[1].Likes);
        Assert.Equal(0, results[2].Downloads);
        Assert.Equal("", results[3].Id); // entry without an id
        Assert.Equal(42, results[3].Downloads);
    }

    [Fact]
    public void Malformed_json_yields_empty_list()
    {
        Assert.Empty(HubClient.ParseModels("not json"));
        Assert.Empty(HubClient.ParseModels(""));
    }

    // ----- HTTP path (stub handler) -----------------------------------------

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> stub mirroring the
    /// HubUserInfoTests style: returns a canned response, records the outgoing
    /// URI, and honors the request token (a canceled token surfaces as a
    /// canceled task, i.e. <see cref="TaskCanceledException"/> through
    /// HttpClient — the same shape MainWindow's catch filter keys on).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = respond;

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

            LastRequestUri = request.RequestUri;
            return Task.FromResult(_respond(request));
        }
    }

    /// <summary>Parses a request URI's query string into unescaped key/value pairs.</summary>
    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return result;
    }

    [Fact]
    public async Task Search_url_defaults_to_limit_30_keeping_filters()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);

        await new HubClient(null).SearchModels(client, "gemma 3 GGUF");

        var q = ParseQuery(handler.LastRequestUri!);
        Assert.Equal("30", q["limit"]);
        // Uri.EscapeDataString + query parsing round-trips the raw query.
        Assert.Equal("gemma 3 GGUF", q["search"]);
        Assert.Equal("gguf", q["filter"]);
        Assert.Equal("downloads", q["sort"]);
        Assert.Equal("-1", q["direction"]);
    }

    [Fact]
    public async Task Search_url_honors_explicit_limit_for_suggestions()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);

        await new HubClient(null).SearchModels(client, "qwen", limit: 6);

        var q = ParseQuery(handler.LastRequestUri!);
        Assert.Equal("6", q["limit"]);
        Assert.Equal("qwen", q["search"]);
        Assert.Equal("gguf", q["filter"]);
        Assert.Equal("downloads", q["sort"]);
        Assert.Equal("-1", q["direction"]);
    }

    [Fact]
    public void Shared_search_client_is_reused_with_ten_second_timeout()
    {
        // Same instance across accesses (the public overload routes through it,
        // so concurrent keystroke bursts reuse one client instead of churning).
        Assert.Same(HubClient.SharedClient, HubClient.SharedClient);
        Assert.Equal(TimeSpan.FromSeconds(10), HubClient.SharedClient.Timeout);
    }

    [Fact]
    public async Task Pre_cancelled_token_surfaces_as_task_canceled()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // If the token were not plumbed through SearchModels -> GetAsync, the
        // call would succeed and return an empty list instead of throwing.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => new HubClient(null).SearchModels(client, "q", cts.Token));
    }
}
