using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for <see cref="LlamaManager.ExtractServerErrorMessage"/> — the
/// delete-failure path surfaces the server's reason in the UI, so the
/// extraction must be robust against non-JSON and oversized bodies.
/// </summary>
public class LlamaManagerDeleteTests
{
    [Fact]
    public void Extracts_The_Error_Message_From_The_Server_Json()
    {
        // The real router shape for a refused delete (preset/models_dir source).
        var body = "{\"error\":{\"code\":500,\"message\":" +
                   "\"model name=ggml-org/gpt-oss-20b-GGUF:MXFP4 is not removable (not from cache)\"," +
                   "\"type\":\"server_error\"}}";

        Assert.Equal(
            "model name=ggml-org/gpt-oss-20b-GGUF:MXFP4 is not removable (not from cache)",
            LlamaManager.ExtractServerErrorMessage(body));
    }

    [Fact]
    public void Falls_Back_To_The_Raw_Body_When_It_Is_Not_Json()
    {
        Assert.Equal("boom", LlamaManager.ExtractServerErrorMessage("boom"));
    }

    [Fact]
    public void Falls_Back_When_The_Json_Has_No_Error_Message()
    {
        Assert.Equal("{\"success\":false}",
            LlamaManager.ExtractServerErrorMessage("{\"success\":false}"));
    }

    [Fact]
    public void Truncates_An_Oversized_Reason()
    {
        var body = "{\"error\":{\"message\":\"" + new string('x', 500) + "\"}}";
        var result = LlamaManager.ExtractServerErrorMessage(body);

        Assert.Equal(141, result.Length); // 140 chars + the ellipsis
        Assert.EndsWith("…", result);
    }
}
