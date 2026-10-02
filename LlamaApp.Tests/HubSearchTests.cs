using System.Net;
using System.Text;
using LlamaApp.HuggingFace;
using LlamaApp.Views;
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

        /// <summary>The outgoing request's Authorization header, as sent (null when anonymous).</summary>
        public string? LastAuthHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

            LastRequestUri = request.RequestUri;
            var auth = request.Headers.Authorization;
            LastAuthHeader = auth is null ? null : auth.Scheme + " " + auth.Parameter;
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
    public async Task Search_sends_the_token_as_bearer_auth_when_configured()
    {
        // A configured token lifts the Hub's anonymous rate limits — the
        // 429 status-line guidance depends on this actually happening.
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);

        await new HubClient("hf_test_token").SearchModels(client, "gemma");

        Assert.Equal("Bearer hf_test_token", handler.LastAuthHeader);
    }

    [Fact]
    public async Task Search_stays_anonymous_without_a_token()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);

        await new HubClient(null).SearchModels(client, "gemma");

        Assert.Null(handler.LastAuthHeader);
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

    // ----- Pagination: skip param propagation -------------------------------

    [Fact]
    public async Task Search_url_propagates_skip_keeping_limit_and_filters()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);

        await new HubClient(null).SearchModels(client, "gemma 3 GGUF", limit: 30, skip: 30);

        var q = ParseQuery(handler.LastRequestUri!);
        Assert.Equal("30", q["skip"]);
        Assert.Equal("30", q["limit"]);
        // The pagination offset must not disturb the other params.
        Assert.Equal("gemma 3 GGUF", q["search"]);
        Assert.Equal("gguf", q["filter"]);
        Assert.Equal("downloads", q["sort"]);
        Assert.Equal("-1", q["direction"]);
    }

    [Fact]
    public async Task Search_url_omits_skip_when_zero()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        using var client = new HttpClient(handler);

        // Default call (skip defaults to 0): the URL stays byte-identical to
        // the pre-pagination first-page request — skip is not emitted.
        await new HubClient(null).SearchModels(client, "gemma");

        var q = ParseQuery(handler.LastRequestUri!);
        Assert.False(q.ContainsKey("skip"));
        Assert.Equal("30", q["limit"]);
    }

    // ----- Pagination: pure helpers -----------------------------------------

    [Fact]
    public void Possibly_has_next_page_only_when_page_is_full()
    {
        Assert.True(HubSearchPagination.PossiblyHasNextPage(30, 30));  // full page
        Assert.False(HubSearchPagination.PossiblyHasNextPage(29, 30)); // short page
        Assert.False(HubSearchPagination.PossiblyHasNextPage(0, 30));  // no results
        Assert.True(HubSearchPagination.PossiblyHasNextPage(6, 6));    // full suggestions page
        Assert.False(HubSearchPagination.PossiblyHasNextPage(5, 6));   // short suggestions page
        Assert.False(HubSearchPagination.PossiblyHasNextPage(1, 0));   // guard: non-positive page size
    }

    [Fact]
    public void Next_skip_counts_result_rows_not_the_load_more_sentinel()
    {
        var rows = new List<HubModelItemViewModel>();
        for (var i = 0; i < 30; i++)
            rows.Add(new HubModelItemViewModel { RepoId = $"org/repo-{i}" });
        rows.Add(new HubModelItemViewModel { IsLoadMoreRow = true });

        // The click handler fetches at skip = real rows shown; the sentinel
        // must not shift the offset (it would skip a result forever).
        Assert.Equal(30, MainWindow.CountHubResultRows(rows));
        Assert.Equal(30, HubSearchPagination.NextSkip(MainWindow.CountHubResultRows(rows)));
    }

    // ----- Typed non-success status (429/401/403/500) -----------------------

    [Theory]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Non_success_status_throws_typed_hub_search_exception(int status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HubSearchException>(
            () => new HubClient(null).SearchModels(client, "q"));

        // The numeric status is surfaced in a parseable, typed way.
        Assert.Equal(status, ex.Status);
        // And it is still an HttpRequestException, so the existing catch
        // filters (suggestions catch-all, load-more network filter) keep
        // catching it — the throw-on-failure contract is preserved.
        Assert.IsAssignableFrom<HttpRequestException>(ex);
        // The request was actually issued (URL shape unchanged).
        Assert.NotNull(handler.LastRequestUri);
    }
}
