using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaApp.HuggingFace;

/// <summary>
/// A non-success response from a Hugging Face Hub search
/// (<c>GET /api/models</c>), carrying the numeric HTTP
/// <see cref="Status"/> so callers can tell an actionable rejection
/// (429 rate limit, 401/403 auth) apart from an unparseable transport
/// failure. Derives from <see cref="HttpRequestException"/> so existing
/// catch filters keep catching it, while the typed status lets the search
/// status line classify it first.
/// </summary>
public sealed class HubSearchException : HttpRequestException
{
    /// <summary>The HTTP status code the Hub returned.</summary>
    public int Status { get; }

    public HubSearchException(int statusCode)
        : base($"Response status code does not indicate success: {statusCode} ({(HttpStatusCode)statusCode}).",
               inner: null, statusCode: (HttpStatusCode)statusCode)
        => Status = statusCode;
}

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

        /// <summary>When the repo was last updated (null when the Hub omits
        /// it or the timestamp doesn't parse) — surfaced as a relative age so
        /// a stale mirror is recognizable at a glance.</summary>
        public DateTimeOffset? LastModified { get; init; }
    }

    /// <summary>Default page size for a full Hub search (suggestions pass 6).</summary>
    public const int DefaultSearchLimit = 30;

    /// <summary>
    /// Searches the Hub for GGUF models matching <paramref name="query"/>,
    /// most-downloaded first. An empty query returns without a request; a
    /// network failure or a non-success status throws — the caller decides
    /// how to surface it (the search box shows an inline error).
    /// <paramref name="skip"/> is a 0-based offset for pagination: 0 fetches
    /// the first page, a later page passes the number of results already shown.
    /// </summary>
    public async Task<List<HubSearchResult>> SearchModels(
        string query, CancellationToken cancel = default, int limit = DefaultSearchLimit, int skip = 0)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        return await SearchModels(Http, query, cancel, limit, skip);
    }

    // Split from the public overload so tests can drive the HTTP path with a
    // mock handler; the public overload routes through the shared static client.
    internal async Task<List<HubSearchResult>> SearchModels(
        HttpClient client, string query, CancellationToken cancel = default, int limit = DefaultSearchLimit, int skip = 0)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // filter=gguf restricts to repos tagged as shipping GGUF files —
        // the only form the llama server can fetch. sort=downloads ranks
        // the (otherwise relevance-ordered) results by popularity. limit caps
        // the payload (full search uses the 30 default; suggestions pass 6).
        // skip is a 0-based offset, emitted only when >0 so the first-page URL
        // stays byte-identical for the frozen full-search/suggestion paths.
        var url = $"{HUGGINGFACE_HUB_BASE_URL}/models" +
                  $"?search={Uri.EscapeDataString(query)}" +
                  $"&filter=gguf&sort=downloads&direction=-1&limit={limit}" +
                  (skip > 0 ? $"&skip={skip}" : "");

        // Auth goes per-request, not on the shared client: the client is
        // reused across callers (and token changes between them), and a
        // token lifts the Hub's anonymous rate limits — which is exactly
        // what the 429 guidance in the status line promises. Search is
        // unauthenticated-only otherwise; a rejected token surfaces as a
        // 401/403 and the status line handles it.
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await client.SendAsync(req, cancel);
        // Surface the numeric status in a typed way so the caller can
        // distinguish 429/401/403 from a generic transport error; still
        // throws on every non-success (the throw-on-failure contract the
        // suggestions and full-search callers rely on).
        if (!resp.IsSuccessStatusCode) throw new HubSearchException((int)resp.StatusCode);
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

    // Avatar URL resolution, tried in order (verified against the live Hub —
    // the historical /api/users/{name}/avatarUrl endpoint no longer exists
    // for anyone, which silently blanked every avatar):
    //   1. GET /api/users/{name}/avatar → {"avatarUrl": "…"} — users, cheap.
    //   2. GET /api/organizations/{name}/overview → {"avatarUrl": "…", …} —
    //      organizations are NOT served under /api/users (a 404 there says
    //      "This user does not exist"), so the org lookup is the fallback.
    // The avatarUrl points at the cdn-avatars CDN; the bytes are fetched
    // separately (its content-type header says webp even for PNG/JPEG
    // payloads — the image decoder sniffs the real format from the bytes).
    private async Task<string?> ResolveUserAvatarUrlAsync(
        HttpClient client, string userName, CancellationToken cancel)
    {
        var userUrl = await ResolveAvatarUrlAsync(
            client, $"{HUGGINGFACE_HUB_BASE_URL}/users/{Uri.EscapeDataString(userName)}/avatar", cancel);
        if (!string.IsNullOrWhiteSpace(userUrl)) return userUrl;

        // Not a user (or a user with no avatar) — try as an organization.
        return await ResolveAvatarUrlAsync(
            client, $"{HUGGINGFACE_HUB_BASE_URL}/organizations/{Uri.EscapeDataString(userName)}/overview", cancel);
    }

    /// <summary>Fetches one avatar-URL endpoint and extracts its avatarUrl;
    /// a non-success status (a 404 for the wrong actor kind is the expected
    /// path) or malformed JSON yields null so the caller's fallback runs.</summary>
    private static async Task<string?> ResolveAvatarUrlAsync(
        HttpClient client, string url, CancellationToken cancel)
    {
        try
        {
            using var resp = await client.GetAsync(url, cancel);

            // An actor without an avatar (404) is an expected answer, not an error.
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cancel);
            return JsonSerializer.Deserialize<UserAvatarDto>(json)?.AvatarUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Avatar-URL DTO — the key both endpoints share.</summary>
    internal sealed class UserAvatarDto
    {
        [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
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
        LastModified = ParseTimestamp(dto.LastModified),
    };

    /// <summary>ISO-8601 wire timestamp → instant; null when absent/malformed
    /// (a broken timestamp must not cost the row its other metadata).</summary>
    private static DateTimeOffset? ParseTimestamp(string? text)
        => DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

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
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
    }
}
