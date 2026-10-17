using System.Net;
using System.Text;
using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the Hub avatar resolution chain behind
/// <see cref="HubClient.GetUserAvatarBytesAsync"/>: users resolve via
/// /api/users/{name}/avatar, organizations via /api/organizations/{name}/overview
/// (the historical /api/users/{name}/avatarUrl endpoint is gone — verified
/// live, it 404s for users and orgs alike, which silently blanked every
/// avatar). The avatarUrl points at the CDN; its bytes are fetched separately.
/// </summary>
public class HubAvatarTests
{
    private const string UserAvatarUrl = "https://cdn-avatars.huggingface.co/v1/user.jpeg";
    private const string OrgAvatarUrl = "https://cdn-avatars.huggingface.co/v1/org.png";
    private static readonly byte[] UserBytes = [1, 2, 3];
    private static readonly byte[] OrgBytes = [4, 5, 6];

    /// <summary>Routes the avatar endpoints + CDN URLs; records every request.</summary>
    private sealed class AvatarStub : HttpMessageHandler
    {
        public required Func<string, HttpResponseMessage> Respond;
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(Respond(url));
        }

        public bool Requested(string fragment) => Requests.Any(r => r.Contains(fragment, StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    [Fact]
    public async Task Users_Resolve_Via_The_User_Avatar_Endpoint()
    {
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/moritz/avatar") => Json($$"""{"avatarUrl":"{{UserAvatarUrl}}"}"""),
                var u when u.Contains("cdn-avatars") => Bytes(UserBytes),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "moritz");

        Assert.Equal(UserBytes, bytes);
        // The org fallback must not fire when the user lookup answers.
        Assert.False(handler.Requested("/organizations/"));
    }

    [Fact]
    public async Task Organizations_Resolve_Via_The_Organization_Overview_Endpoint()
    {
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/ggml-org/avatar") => Json("""{"error":"This user does not exist"}""", HttpStatusCode.NotFound),
                var u when u.Contains("/organizations/ggml-org/overview") => Json($$"""{"_id":"x","avatarUrl":"{{OrgAvatarUrl}}","name":"ggml-org"}"""),
                var u when u.Contains("cdn-avatars") => Bytes(OrgBytes),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "ggml-org");

        Assert.Equal(OrgBytes, bytes);
        Assert.True(handler.Requested("/organizations/ggml-org/overview"));
    }

    [Fact]
    public async Task Neither_A_User_Nor_An_Organization_Yields_Null()
    {
        var handler = new AvatarStub
        {
            Respond = _ => Json("""{"error":"not found"}""", HttpStatusCode.NotFound),
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "nobody");

        Assert.Null(bytes);
    }

    [Fact]
    public async Task A_User_Endpoint_Without_An_AvatarUrl_Falls_Back_To_The_Org_Lookup()
    {
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/someone/avatar") => Json("{}"), // 200, no avatarUrl
                var u when u.Contains("/organizations/someone/overview") => Json($$"""{"avatarUrl":"{{OrgAvatarUrl}}"}"""),
                var u when u.Contains("cdn-avatars") => Bytes(OrgBytes),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "someone");

        Assert.Equal(OrgBytes, bytes);
    }

    [Fact]
    public async Task Malformed_Avatar_Json_Fails_Soft_To_Null()
    {
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/avatar") || u.Contains("/overview") => Json("not json"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "weird");

        Assert.Null(bytes);
    }

    [Theory]
    [InlineData("https://cdn-avatars.huggingface.co/v1/user.jpeg", true)]
    [InlineData("https://huggingface.co/front/assets/hf.png", true)]
    [InlineData("https://hf.co/v1/user.jpeg", true)]
    [InlineData("http://cdn-avatars.huggingface.co/v1/user.jpeg", false)]  // cleartext
    [InlineData("https://evil.com/avatar.png", false)]                     // off-site
    [InlineData("https://evilhuggingface.co/avatar.png", false)]           // suffix trick
    [InlineData("https://huggingface.co.evil.com/a.png", false)]           // prefix trick
    [InlineData("file:///C:/avatar.png", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Avatar_Url_Policy_Allows_Only_Https_HuggingFace_Hosts(string? url, bool trusted)
        => Assert.Equal(trusted, HubClient.IsTrustedAvatarUrl(url));

    [Fact]
    public void Who_Am_I_Parses_Only_Trusted_Avatar_Urls()
    {
        var trusted = HubClient.HubUserInfoClient.Parse(
            """{"id":"1","name":"moritz","avatarUrl":"https://cdn-avatars.huggingface.co/v1/user.jpeg"}""");
        var untrusted = HubClient.HubUserInfoClient.Parse(
            """{"id":"1","name":"moritz","avatarUrl":"https://evil.com/steal.png"}""");

        Assert.Equal("https://cdn-avatars.huggingface.co/v1/user.jpeg", trusted!.AvatarUrl);
        Assert.Equal("", untrusted!.AvatarUrl);
    }

    [Fact]
    public async Task An_Untrusted_Resolved_Url_Is_Never_Fetched()
    {
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/moritz/avatar") => Json("""{"avatarUrl":"https://evil.com/steal"}"""),
                _ => Bytes(UserBytes), // answers anything — the point is that nothing else is asked
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "moritz");

        Assert.Null(bytes);
        Assert.False(handler.Requested("evil.com"));
    }

    [Fact]
    public async Task An_Oversized_Avatar_Is_Rejected_By_The_Declared_Length()
    {
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/moritz/avatar") => Json($$"""{"avatarUrl":"{{UserAvatarUrl}}"}"""),
                var u when u.Contains("cdn-avatars") => Bytes(new byte[2 * 1024 * 1024]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "moritz");

        Assert.Null(bytes);
    }

    [Fact]
    public async Task A_Lying_Content_Length_Is_Caught_By_The_Streaming_Cap()
    {
        // Content-Length claims "tiny", the body is 2 MB — the streamed cap
        // must stop the read instead of trusting the header.
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/moritz/avatar") => Json($$"""{"avatarUrl":"{{UserAvatarUrl}}"}"""),
                var u when u.Contains("cdn-avatars") => LyingContentLength(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "moritz");

        Assert.Null(bytes);
    }

    [Fact]
    public async Task A_Redirect_Escape_Is_Rejected_By_The_Final_Url_Check()
    {
        // What a redirect off the trusted set looks like once the handler has
        // followed it: the response's final request URI points elsewhere, and
        // not a byte of the body may be read.
        var handler = new AvatarStub
        {
            Respond = url => url switch
            {
                var u when u.Contains("/users/moritz/avatar") => Json($$"""{"avatarUrl":"{{UserAvatarUrl}}"}"""),
                var u when u.Contains("cdn-avatars") => EscapedRedirect(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }
        };
        using var client = new HttpClient(handler);

        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(client, "moritz");

        Assert.Null(bytes);
    }

    /// <summary>2 MB body with a Content-Length header claiming 1 byte.</summary>
    private static HttpResponseMessage LyingContentLength()
    {
        var content = new ByteArrayContent(new byte[2 * 1024 * 1024]);
        content.Headers.ContentLength = 1;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>The CDN response of a redirect that escaped to an untrusted
    /// host: the final request URI is what the fetch validates.</summary>
    private static HttpResponseMessage EscapedRedirect()
    {
        var resp = Bytes(UserBytes);
        resp.RequestMessage = new HttpRequestMessage(
            HttpMethod.Get, "http://169.254.169.254/latest/meta-data/");
        return resp;
    }
}
