using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaApp.HuggingFace;

public class HubClient(string? token)
{
    private static string HUGGINGFACE_HUB_BASE_URL = "https://huggingface.co/api";

    // Shared client for search: the API base is constant and search is
    // unauthenticated, so one thread-safe instance is safely reused across
    // concurrent callers (suggestions + full search) instead of constructing
    // a new client/socket per keystroke burst. Deliberately credential-free —
    // never add DefaultRequestHeaders/auth here; any future auth must go
    // per-request (like WhoAmI's Bearer header). 10 s timeout matches the
    // per-call clients used elsewhere in this class.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Test-only accessor for the shared search client (identity / timeout assertions).</summary>
    internal static HttpClient SharedClient => Http;

    public sealed class HubUserInfoClient(string baseUrl, string? token)
    {
        /// <summary>
        /// The authenticated user's profile, distilled from the whoami-v2
        /// response to just the fields the UI needs.
        /// </summary>
        public record UserInfo
        {
            /// <summary>Internal user id (Mongo ObjectId) — NOT routable on the website.</summary>
            public string Id = "";

            /// <summary>Username — the public profile lives at https://hf.co/&lt;Name&gt;.</summary>
            public string Name = "";

            /// <summary>Avatar image URL (CDN), empty when the user has none.</summary>
            public string AvatarUrl = "";
        };

        private string Url { get; } = baseUrl;

        /// <summary>
        /// GET /whoami-v2 with the configured Bearer token. Returns null when
        /// no token is provided (no request is made), when the token is
        /// rejected, or on any network/parse failure — the caller's avatar is
        /// a best-effort decoration and must never fault the app.
        /// </summary>
        public async Task<UserInfo?> WhoAmI(CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return await WhoAmI(client, cancel);
        }

        // Split from the public overload so tests can drive the HTTP path with
        // a mock handler (the public overload owns its short-lived client).
        internal async Task<UserInfo?> WhoAmI(HttpClient client, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{Url}/whoami-v2");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await client.SendAsync(req, cancel);

                // A rejected/expired token (401/403) is an expected answer, not
                // an error — the user simply isn't authenticated.
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync(cancel);
                return Parse(json);
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        /// <summary>
        /// whoami-v2 response DTO — only the fields <see cref="UserInfo"/>
        /// needs; the rest of the (large) payload (auth token details, orgs,
        /// billing, …) is ignored by the deserializer.
        /// </summary>
        internal sealed class UserInfoDto
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
        }

        /// <summary>
        /// Parses a whoami-v2 JSON payload into <see cref="UserInfo"/>. Returns
        /// null on malformed JSON; missing fields map to empty strings.
        /// </summary>
        internal static UserInfo? Parse(string json)
        {
            try
            {
                var dto = JsonSerializer.Deserialize<UserInfoDto>(json);
                return dto is null ? null : new UserInfo
                {
                    Id = dto.Id ?? "",
                    Name = dto.Name ?? "",
                    AvatarUrl = dto.AvatarUrl ?? "",
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public HubUserInfoClient UserInfo { get; } = new (HUGGINGFACE_HUB_BASE_URL, token);

    /// <summary>
    /// One Hugging Face Hub repository matching a search — distilled from
    /// the <c>GET /api/models</c> response to just the fields the models
    /// panel needs. Only repos with GGUF files are returned (the request
    /// filters on the <c>gguf</c> tag), so every result is downloadable by
    /// the llama server.
    /// </summary>
    public sealed record HubSearchResult
    {
        /// <summary>Hugging Face repo id, e.g. "ggml-org/gemma-3-4b-it-GGUF".</summary>
        public required string Id { get; init; }

        /// <summary>Download count over all time (0 when the Hub omits it).</summary>
        public long Downloads { get; init; }

        /// <summary>Like count (0 when the Hub omits it).</summary>
        public long Likes { get; init; }
    }

    /// <summary>
    /// Searches the Hub for GGUF models matching <paramref name="query"/>,
    /// most-downloaded first. An empty query returns without a request; a
    /// network failure or a non-success status throws — the caller decides
    /// how to surface it (the search box shows an inline error).
    /// </summary>
    public async Task<List<HubSearchResult>> SearchModels(
        string query, CancellationToken cancel = default, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        return await SearchModels(Http, query, cancel, limit);
    }

    // Split from the public overload so tests can drive the HTTP path with a
    // mock handler; the public overload routes through the shared static client.
    internal async Task<List<HubSearchResult>> SearchModels(
        HttpClient client, string query, CancellationToken cancel = default, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // filter=gguf restricts to repos tagged as shipping GGUF files —
        // the only form the llama server can fetch. sort=downloads ranks
        // the (otherwise relevance-ordered) results by popularity. limit caps
        // the payload (full search uses the 30 default; suggestions pass 6).
        var url = $"{HUGGINGFACE_HUB_BASE_URL}/models" +
                  $"?search={Uri.EscapeDataString(query)}" +
                  $"&filter=gguf&sort=downloads&direction=-1&limit={limit}";

        using var resp = await client.GetAsync(url, cancel);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancel);
        return ParseModels(json);
    }

    /// <summary>
    /// Fetches a Hub user's/org's avatar image bytes: GET
    /// <c>/api/users/{name}/avatarUrl</c> resolves the CDN URL, a second
    /// request fetches the bytes. Returns null when the user has no avatar
    /// or on any network/parse failure — avatars are a best-effort
    /// decoration and must never fault the caller.
    /// </summary>
    public async Task<byte[]?> GetUserAvatarBytesAsync(string userName, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await GetUserAvatarBytesAsync(client, userName, cancel);
    }

    // Split from the public overload so tests can drive the HTTP path with
    // a mock handler (the public overload owns its short-lived client).
    internal async Task<byte[]?> GetUserAvatarBytesAsync(
        HttpClient client, string userName, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;

        try
        {
            var avatarUrl = await ResolveUserAvatarUrlAsync(client, userName, cancel);
            if (string.IsNullOrWhiteSpace(avatarUrl)) return null;
            return await client.GetByteArrayAsync(avatarUrl, cancel);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // GET /api/users/{name}/avatarUrl → {"avatarUrl": "…", "type": "user"|"org"}.
    // The avatarUrl points at the cdn-avatars CDN — fetch the bytes separately.
    private async Task<string?> ResolveUserAvatarUrlAsync(
        HttpClient client, string userName, CancellationToken cancel)
    {
        using var resp = await client.GetAsync(
            $"{HUGGINGFACE_HUB_BASE_URL}/users/{Uri.EscapeDataString(userName)}/avatarUrl", cancel);

        // A user/org without an avatar (404) is an expected answer, not an error.
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(cancel);
        try
        {
            return JsonSerializer.Deserialize<UserAvatarDto>(json)?.AvatarUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>/api/users/{name}/avatarUrl response DTO.</summary>
    internal sealed class UserAvatarDto
    {
        [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    /// <summary>
    /// Parses a <c>GET /api/models</c> JSON array into <see cref="HubSearchResult"/>
    /// records. Malformed JSON or null yields an empty list; missing fields map
    /// to their zero values. Internal for unit tests.
    /// </summary>
    internal static List<HubSearchResult> ParseModels(string json)
    {
        try
        {
            var dtos = JsonSerializer.Deserialize<HubModelDto[]>(json);
            return dtos is null ? [] : [.. dtos.Select(ParseModel)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static HubSearchResult ParseModel(HubModelDto dto) => new()
    {
        Id = dto.Id ?? "",
        Downloads = dto.Downloads ?? 0,
        Likes = dto.Likes ?? 0,
    };

    /// <summary>
    /// /api/models response DTO — only the fields the search list renders;
    /// the rest of the payload (siblings, tags, config, …) is ignored.
    /// Nullable so short entries (an id alone) still deserialize.
    /// </summary>
    internal sealed class HubModelDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("downloads")] public long? Downloads { get; set; }
        [JsonPropertyName("likes")] public long? Likes { get; set; }
    }
}
