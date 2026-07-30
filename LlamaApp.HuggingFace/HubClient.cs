using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaApp.HuggingFace;

public class HubClient(string? token)
{
    private static string HUGGINGFACE_HUB_BASE_URL = "https://huggingface.co/api";

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
}
