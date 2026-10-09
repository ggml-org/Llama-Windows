using System.Text.Json;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Pins the JSON contract of the GitHub <c>releases/latest</c> response used
/// by <see cref="RuntimeUpdateScheduler"/>. The field is snake_case
/// (<c>tag_name</c>); without an explicit <c>[JsonPropertyName]</c> the
/// default (case-sensitive, no naming policy) deserializer silently leaves
/// the tag null and the weekly llama.cpp update never fires — a regression
/// the pure tag parser tests cannot catch. These tests exercise the whole
/// body-to-build-number path so that binding gap stays closed.
/// </summary>
public class RuntimeUpdateReleaseJsonTests
{
    [Fact]
    public void Parses_SnakeCase_TagName()
    {
        Assert.Equal(6726u, RuntimeUpdateScheduler.ParseLatestReleaseBuild("{\"tag_name\":\"b6726\"}"));
    }

    [Fact]
    public void Parses_RealRelease_Shape_IgnoringUnknownFields()
    {
        // Trimmed real response shape (assets/body/author/… ignored).
        const string json = """
            {
              "url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/123",
              "tag_name": "b9553",
              "name": "b9553",
              "draft": false,
              "prerelease": false,
              "published_at": "2025-01-01T00:00:00Z"
            }
            """;

        Assert.Equal(9553u, RuntimeUpdateScheduler.ParseLatestReleaseBuild(json));
    }

    [Fact]
    public void CamelCase_TagName_DoesNotBind()
    {
        // Guards against someone "simplifying" the DTO: the real payload is
        // snake_case, and the default deserializer is case-sensitive.
        Assert.Null(RuntimeUpdateScheduler.ParseLatestReleaseBuild("{\"tagName\":\"b6726\"}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_Body_Yields_No_Build(string? json)
    {
        Assert.Null(RuntimeUpdateScheduler.ParseLatestReleaseBuild(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"tag_name\":\"v1.2.3\"}")]
    [InlineData("{\"tag_name\":\"b6726-rc1\"}")]
    public void NonBuild_Tag_Yields_No_Build(string json)
    {
        Assert.Null(RuntimeUpdateScheduler.ParseLatestReleaseBuild(json));
    }

    [Fact]
    public void Malformed_Json_Throws_For_Caller_To_Classify()
    {
        // The network path catches JsonException and treats it as a failed
        // (still-due) check; keep that contract explicit.
        Assert.Throws<JsonException>(() =>
            RuntimeUpdateScheduler.ParseLatestReleaseBuild("not json"));
    }
}
