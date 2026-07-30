using System.Net;
using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="HubClient.HubUserInfoClient.WhoAmI()"/>: the
/// no-token short-circuit, the Bearer request shape, and the whoami-v2 JSON
/// → <see cref="HubClient.HubUserInfoClient.UserInfo"/> mapping.
/// </summary>
public class HubUserInfoTests
{
    // A whoami-v2 payload with the full documented structure — the nested
    // auth/orgs noise must be ignored and only id/name/avatarUrl mapped.
    private const string WhoAmIJson = """
        {
          "auth": {
            "type": "accessToken",
            "accessToken": {
              "displayName": "cli",
              "role": "read",
              "fineGrained": {
                "scoped": [
                  {
                    "entity": { "_id": "abc", "name": "ds", "type": "dataset" },
                    "permissions": [ "read" ]
                  }
                ],
                "global": [],
                "canReadGatedRepos": true
              },
              "createdAt": "2026-07-30T10:37:06.105Z"
            },
            "expiresAt": "2026-07-30T10:37:06.105Z",
            "resource": { "sub": "65f1a2b3c4d5e6f7a8b9c0d1" }
          },
          "type": "user",
          "id": "65f1a2b3c4d5e6f7a8b9c0d1",
          "name": "momo",
          "fullname": "Momo Dev",
          "email": "momo@example.com",
          "canPay": true,
          "billingMode": "prepaid",
          "avatarUrl": "https://cdn-avatars.huggingface.co/v1/production/uploads/abc/avatar.png",
          "periodEnd": 1,
          "emailVerified": true,
          "isPro": true,
          "orgs": [
            {
              "type": "org",
              "id": "org-id",
              "name": "acme",
              "fullname": "Acme Inc",
              "email": "hi@acme.com",
              "canPay": true,
              "billingMode": "prepaid",
              "avatarUrl": "https://example.com/org.png",
              "periodEnd": 1,
              "plan": "team",
              "roleInOrg": "admin",
              "securityRestrictions": [],
              "resourceGroups": [ { "id": "rg", "name": "rg", "role": "admin" } ]
            }
          ]
        }
        """;

    // ----- No token ---------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WhoAmI_Without_Token_Returns_Null(string? token)
    {
        var info = await new HubClient(token).UserInfo.WhoAmI();
        Assert.Null(info);
    }

    // ----- HTTP path (mock handler) -----------------------------------------

    [Fact]
    public async Task WhoAmI_Sends_Bearer_Token_To_WhoAmI_V2_And_Maps_Response()
    {
        string? auth = null;
        Uri? requestUri = null;
        var client = new HttpClient(new StubHandler(req =>
        {
            auth = req.Headers.Authorization?.ToString();
            requestUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WhoAmIJson),
            };
        }));

        var info = await new HubClient("hf_secret").UserInfo.WhoAmI(client);

        Assert.Equal("Bearer hf_secret", auth);
        Assert.Equal("https://huggingface.co/api/whoami-v2", requestUri?.ToString());
        Assert.NotNull(info);
        Assert.Equal("65f1a2b3c4d5e6f7a8b9c0d1", info.Id);
        Assert.Equal("momo", info.Name);
        Assert.Equal("https://cdn-avatars.huggingface.co/v1/production/uploads/abc/avatar.png", info.AvatarUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task WhoAmI_Non_Success_Status_Returns_Null(HttpStatusCode status)
    {
        var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        var info = await new HubClient("hf_secret").UserInfo.WhoAmI(client);
        Assert.Null(info);
    }

    // ----- Parse ------------------------------------------------------------

    [Fact]
    public void Parse_Missing_Fields_Map_To_Empty_Strings()
    {
        var info = HubClient.HubUserInfoClient.Parse("""{ "type": "user" }""");

        Assert.NotNull(info);
        Assert.Equal("", info.Id);
        Assert.Equal("", info.Name);
        Assert.Equal("", info.AvatarUrl);
    }

    [Fact]
    public void Parse_Malformed_Json_Returns_Null()
    {
        Assert.Null(HubClient.HubUserInfoClient.Parse("not json"));
    }

    // ----- Stub -------------------------------------------------------------

    /// <summary>Minimal HttpMessageHandler stub: returns a canned response and
    /// lets the test inspect the outgoing request inside the delegate (the
    /// request is disposed by the caller after SendAsync).</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
