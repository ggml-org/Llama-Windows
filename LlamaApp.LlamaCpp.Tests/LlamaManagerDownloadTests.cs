using Xunit;
using LlamaApp.Llama;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the download-event matching rules in
/// <see cref="LlamaManager"/>: SSE events must be matched to the download
/// that started them even though the server ids a model by its bare repo
/// while the download is in flight (the quant resolves only on completion),
/// while the download itself is started with the repo:quant form.
/// </summary>
public class LlamaManagerDownloadTests
{
    [Fact]
    public void Exact_Id_Matches()
        => Assert.True(LlamaManager.SameDownloadModel(
            "ggml-org/gemma-3-4b-it-GGUF:Q4_0", "ggml-org/gemma-3-4b-it-GGUF:Q4_0"));

    [Fact]
    public void Exact_Id_Match_Is_Case_Insensitive()
        => Assert.True(LlamaManager.SameDownloadModel(
            "ggml-org/gemma-3-4b-it-GGUF:q4_0", "ggml-org/gemma-3-4b-it-GGUF:Q4_0"));

    [Fact]
    public void Bare_Repo_Event_Matches_RepoQuant_Download()
        => Assert.True(LlamaManager.SameDownloadModel(
            "ggml-org/gemma-3-4b-it-GGUF", "ggml-org/gemma-3-4b-it-GGUF:Q4_0"));

    [Fact]
    public void Broadcast_Event_Matches_Everything()
        => Assert.True(LlamaManager.SameDownloadModel(
            "*", "ggml-org/gemma-3-4b-it-GGUF:Q4_0"));

    [Fact]
    public void Another_Model_Does_Not_Match()
        => Assert.False(LlamaManager.SameDownloadModel(
            "ggml-org/gpt-oss-20b-GGUF:Q8_0", "ggml-org/gemma-3-4b-it-GGUF:Q4_0"));

    [Fact]
    public void Another_Repo_Does_Not_Match_Without_Quant()
        => Assert.False(LlamaManager.SameDownloadModel(
            "ggml-org/gpt-oss-20b-GGUF", "ggml-org/gemma-3-4b-it-GGUF:Q4_0"));
}
