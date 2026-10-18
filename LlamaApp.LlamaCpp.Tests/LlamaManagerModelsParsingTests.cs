using System.Text;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the <c>GET /models</c> body parser's failure-vs-empty distinction
/// (<see cref="LlamaManager.ParseModelsAsync"/>). The supervisor's death
/// detection depends on it: a FAILED fetch (null) is confirmed with a
/// <c>/health</c> probe and can declare the server failed, while an empty model
/// list is a valid answer from a living server and must never be confused with
/// an error.
/// </summary>
public class LlamaManagerModelsParsingTests
{
    private static Task<IReadOnlyList<LlamaManager.ServerModel>?> Parse(string json)
        => LlamaManager.ParseModelsAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task Valid_Body_Maps_Models()
    {
        var parsed = await Parse(
            """{"data":[{"id":"ggml-org/gemma-3-4b-it-GGUF:Q4_K_M","path":"/m/gemma.gguf","status":{"value":"loaded"},"architecture":{"input_modalities":["text","image"]},"source":"cache","can_remove":true}]}""");

        Assert.NotNull(parsed);
        var m = Assert.Single(parsed!);
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M", m.Id);
        Assert.Equal("/m/gemma.gguf", m.Path);
        Assert.Equal("loaded", m.Status);
        Assert.Equal("cache", m.Source);
        Assert.True(m.CanRemove);
        Assert.True(m.SupportsImage);
    }

    [Fact]
    public async Task Empty_Data_Array_Is_A_Valid_Empty_Snapshot_Not_A_Failure()
    {
        var parsed = await Parse("""{"data":[]}""");
        Assert.NotNull(parsed);
        Assert.Empty(parsed!);
    }

    [Fact]
    public async Task Malformed_Body_Is_A_Failure_Not_An_Empty_Snapshot()
        // Truncated JSON: a proxy error page or a zombie listener, not a model list.
        => Assert.Null(await Parse("""{"data":[{"""));

    [Fact]
    public async Task Missing_Data_Member_Is_A_Failure_Not_An_Empty_Snapshot()
        => Assert.Null(await Parse("""{"models":[]}"""));

    [Fact]
    public async Task Null_Data_Member_Is_A_Failure_Not_An_Empty_Snapshot()
        => Assert.Null(await Parse("""{"data":null}"""));

    [Fact]
    public async Task Wrong_Type_Data_Is_A_Failure_Not_An_Empty_Snapshot()
        => Assert.Null(await Parse("""{"data":42}"""));

    [Fact]
    public async Task A_Failed_Load_Reports_Its_Exit_Code()
    {
        // The exact shape of the incident that started this: the child
        // crashed (CUDA OOM -> fail-fast abort) and the server said so.
        var parsed = await Parse(
            """{"data":[{"id":"org/repo:Q8_0","status":{"value":"unloaded","failed":true,"exit_code":-1073740791}}]}""");

        var m = Assert.Single(parsed!);
        Assert.Equal("unloaded", m.Status);
        Assert.True(m.IsFailed);
        Assert.Equal(-1073740791, m.LoadExitCode);
    }

    [Fact]
    public async Task A_Healthy_Status_Is_Not_Failed()
    {
        var parsed = await Parse(
            """{"data":[{"id":"org/repo:Q8_0","status":{"value":"loaded"}}]}""");

        var m = Assert.Single(parsed!);
        Assert.False(m.IsFailed);
        Assert.Null(m.LoadExitCode);
    }
}
