using System.Text.Json;
using Xunit;
using LlamaApp.Common;
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

    [Fact]
    public void Per_File_Events_Retain_Omitted_Files_And_Move_Aggregate_Forward()
    {
        var aggregate = new ModelDownloadProgressAccumulator();

        var first = Observe(aggregate, """
            {"progress":{
                "model.gguf":{"done":80,"total":100},
                "mmproj.gguf":{"done":20,"total":100}}}
            """);
        var modelOnly = Observe(aggregate, """
            {"progress":{"model.gguf":{"done":90,"total":100}}}
            """);
        var sidecarOnly = Observe(aggregate, """
            {"progress":{"mmproj.gguf":{"done":30,"total":100}}}
            """);

        Assert.Equal((100, 200), (first.DownloadedBytes, first.TotalBytes));
        Assert.Equal((110, 200), (modelOnly.DownloadedBytes, modelOnly.TotalBytes));
        Assert.Equal((120, 200), (sidecarOnly.DownloadedBytes, sidecarOnly.TotalBytes));
        Assert.True(first.Fraction < modelOnly.Fraction);
        Assert.True(modelOnly.Fraction < sidecarOnly.Fraction);
    }

    [Fact]
    public void Late_Sidecar_Discovery_Does_Not_Restart_The_Displayed_Fraction()
    {
        var aggregate = new ModelDownloadProgressAccumulator();
        var model = Observe(aggregate,
            "{\"progress\":{\"model.gguf\":{\"done\":80,\"total\":100}}}");

        var withSidecar = Observe(aggregate, """
            {"progress":{
                "model.gguf":{"done":80,"total":100},
                "drafter.gguf":{"done":0,"total":100}}}
            """);

        Assert.Equal(0.8, model.Fraction, precision: 6);
        Assert.Equal(80, withSidecar.DownloadedBytes); // raw bytes stay truthful
        Assert.Equal(200, withSidecar.TotalBytes);
        Assert.Equal(0.8, withSidecar.Fraction, precision: 6); // never 80% -> 40%
    }

    [Fact]
    public void Empty_And_Regressive_Snapshots_Do_Not_Erase_Known_Progress()
    {
        var aggregate = new ModelDownloadProgressAccumulator();
        var first = Observe(aggregate,
            "{\"progress\":{\"model.gguf\":{\"done\":50,\"total\":100}}}");
        var empty = Observe(aggregate, "{\"progress\":{}}");
        var regressive = Observe(aggregate,
            "{\"progress\":{\"model.gguf\":{\"done\":0,\"total\":0}}}");

        Assert.Equal(0.5, first.Fraction, precision: 6);
        Assert.Equal((50, 100), (empty.DownloadedBytes, empty.TotalBytes));
        Assert.Equal((50, 100), (regressive.DownloadedBytes, regressive.TotalBytes));
        Assert.Equal(first.Fraction, empty.Fraction);
        Assert.Equal(first.Fraction, regressive.Fraction);
    }

    [Fact]
    public void A_Deliberate_Retry_Uses_A_Fresh_Aggregate_And_Can_Reset()
    {
        var firstAttempt = new ModelDownloadProgressAccumulator();
        Assert.Equal(0.75, Observe(firstAttempt,
            "{\"progress\":{\"model.gguf\":{\"done\":75,\"total\":100}}}").Fraction);

        var retry = new ModelDownloadProgressAccumulator();
        var restarted = Observe(retry,
            "{\"progress\":{\"model.gguf\":{\"done\":0,\"total\":100}}}");

        Assert.Equal(0, restarted.DownloadedBytes);
        Assert.Equal(0, restarted.Fraction);
        Assert.False(restarted.Done);
        Assert.False(restarted.Failed);
    }

    private static ModelDownloadProgress Observe(
        ModelDownloadProgressAccumulator aggregate, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return aggregate.Observe("a/b", doc.RootElement);
    }
}
