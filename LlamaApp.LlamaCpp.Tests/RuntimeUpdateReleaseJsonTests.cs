using System.Text.Json;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Pins the JSON contract of the GitHub release responses used by
/// <see cref="RuntimeUpdateScheduler"/> — the <c>releases/latest</c> body and
/// the <c>releases</c> LIST body, which sees the nightly <c>b&lt;build&gt;</c>
/// prereleases since llama.cpp's stable releases adopted semver tags
/// (issue #133). The field is snake_case (<c>tag_name</c>); without an
/// explicit <c>[JsonPropertyName]</c> the default (case-sensitive, no naming
/// policy) deserializer silently leaves the tag null and the weekly llama.cpp
/// update never fires — a regression the pure tag parser tests cannot catch.
/// These tests exercise the whole body-to-build-number path so that binding
/// gap stays closed.
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

    // ---- releases LIST (prereleases included): the nightly b-stream ----

    [Fact]
    public void ReleaseList_SemverStableToppingNightlyPrereleases_YieldsTheNewestBuild()
    {
        // Trimmed real shape since llama.cpp's semver stable releases: the
        // stable "v0.7.0" tops the newest-first list while the comparable
        // b<build> nightlies follow as prereleases (issue #133) — the newest
        // build is the highest b-tag, not the newest entry.
        const string json = """
            [
              {
                "url": "https://api.github.com/repos/ggml-org/llama.cpp/releases/1",
                "tag_name": "v0.7.0",
                "name": "v0.7.0",
                "draft": false,
                "prerelease": false,
                "published_at": "2026-10-15T17:13:19Z"
              },
              {
                "tag_name": "b11856",
                "prerelease": true,
                "published_at": "2026-10-24T15:06:48Z"
              },
              {
                "tag_name": "b11854",
                "prerelease": true,
                "published_at": "2026-10-24T14:35:59Z"
              }
            ]
            """;

        Assert.Equal(11856u, RuntimeUpdateScheduler.ParseLatestBuildFromReleaseList(json));
    }

    [Fact]
    public void ReleaseList_TakesTheHighestBuild_NotTheFirst()
    {
        Assert.Equal(100u, RuntimeUpdateScheduler.ParseLatestBuildFromReleaseList(
            "[{\"tag_name\":\"b20\"},{\"tag_name\":\"b5\"},{\"tag_name\":\"b100\"}]"));
    }

    [Fact]
    public void ReleaseList_LooksPastUnrecognizableTags()
    {
        Assert.Equal(6726u, RuntimeUpdateScheduler.ParseLatestBuildFromReleaseList(
            "[{\"tag_name\":\"v1.2.3\"},{\"tag_name\":\"b6726-rc1\"},{\"tag_name\":\"b6726\"}]"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"tag_name\":\"v0.7.0\"}]")]
    [InlineData("[{}]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReleaseList_WithoutBuildTags_Yields_No_Build(string? json)
    {
        // Only-semver pages fall through to the releases/latest fallback —
        // and then to the safe "skip the check" answer.
        Assert.Null(RuntimeUpdateScheduler.ParseLatestBuildFromReleaseList(json));
    }

    [Fact]
    public void ReleaseList_Malformed_Json_Throws_For_Caller_To_Classify()
    {
        Assert.Throws<JsonException>(() =>
            RuntimeUpdateScheduler.ParseLatestBuildFromReleaseList("not json"));
    }

    [Fact]
    public void ReleaseList_HugeBuildNumber_IsUnparsable_NotAnOverflow()
    {
        // Tags are untrusted input: "b99999999999" is all-digits but exceeds
        // uint — unparsable (skip), never an exception out of the check.
        Assert.Null(RuntimeUpdateScheduler.ParseReleaseBuild("b99999999999"));
        Assert.Null(RuntimeUpdateScheduler.ParseLatestBuildFromReleaseList(
            "[{\"tag_name\":\"b99999999999\"}]"));
    }
}
