using System.Text;
using System.Text.Json;
using LlamaApp.Common;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the JSON schemas exchanged with the local llama.cpp server.
/// Keeps us honest when the server's <c>/models</c>, <c>/models/sse</c>, or
/// status payloads evolve.
/// </summary>
public class LlamaManagerSchemaTests
{
    // ----- GET /models -> ModelsResponseDto -> ServerModel -------------------

    [Fact]
    public void Map_Loaded_Vision_Model_From_Dto()
    {
        var dto = new LlamaManager.ServerModelDto
        {
            Id = "ggml-org/gemma-3-4b-it-GGUF:Q4_K_M",
            Path = "/models/gemma-3-4b-it-qat.q4_k_m.gguf",
            Status = new LlamaManager.ModelStatusDto { Value = "loaded" },
            Architecture = new LlamaManager.ArchitectureDto
            {
                InputModalities = ["text", "image"],
                OutputModalities = ["text"],
            },
            Source = "cache",
            CanRemove = true,
        };

        var model = LlamaManager.Map(dto);

        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M", model.Id);
        Assert.Equal("/models/gemma-3-4b-it-qat.q4_k_m.gguf", model.Path);
        Assert.True(model.IsLoaded);
        Assert.False(model.IsLoading);
        Assert.True(model.SupportsImage);
        Assert.Equal(new[] { "text", "image" }, model.InputModalities);
        Assert.Equal("cache", model.Source);
        Assert.True(model.CanRemove);
    }

    [Fact]
    public void Map_Unloading_Model_Has_No_Vision()
    {
        var dto = new LlamaManager.ServerModelDto
        {
            Id = "ggml-org/gpt-oss-20b-GGUF:Q4_0",
            Status = new LlamaManager.ModelStatusDto { Value = "loading" },
            Source = "cache",
        };

        var model = LlamaManager.Map(dto);

        Assert.True(model.IsLoading);
        Assert.False(model.IsLoaded);
        Assert.False(model.SupportsImage);
        Assert.Empty(model.InputModalities);
    }

    [Fact]
    public void Deserialize_ModelsResponse_And_Map_All_Entries()
    {
        const string json = """
            {"data":[
                {"id":"a/b:Q4","status":{"value":"unloaded"},"source":"cache","can_remove":false},
                {"id":"c/d:Q8","status":{"value":"loaded"},"architecture":{"input_modalities":["text"]},"can_remove":true}
            ]}
            """;

        var doc = JsonSerializer.Deserialize<LlamaManager.ModelsResponseDto>(json);

        Assert.NotNull(doc);
        Assert.NotNull(doc.Data);
        Assert.Equal(2, doc.Data.Count);
        var models = doc.Data.Select(LlamaManager.Map).ToList();
        Assert.Equal("a/b:Q4", models[0].Id);
        Assert.Equal("unloaded", models[0].Status);
        Assert.Equal("c/d:Q8", models[1].Id);
        Assert.True(models[1].IsLoaded);
    }

    // ----- SSE parse + progress aggregation ----------------------------------

    [Fact]
    public async Task ParseSseStream_Yields_Event_Model_And_Data()
    {
        const string payload = """
            data: {"model":"ggml-org/gemma-3-4b-it-GGUF:Q4_K_M","event":"download_finished","data":{}}

            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Single(events);
        var (evt, model, data) = events[0];
        Assert.Equal("download_finished", evt);
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M", model);
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
    }

    [Fact]
    public async Task ParseSseStream_Ignores_Non_Data_Lines()
    {
        const string payload = """
            event: download_progress
            id: 1
            data: {"model":"a/b","event":"download_progress","data":{"progress":{}}}

            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Single(events);
        Assert.Equal("download_progress", events[0].Event);
    }

    [Fact]
    public void SumProgress_Sums_Per_Url_Done_And_Total()
    {
        const string dataJson = """
            {"progress":{
                "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/2714b5519c6c3516b1000e7c5e1eba998dfe1fe8/mmproj-gemma-4-E4B-it-Q8_0.gguf":{"done":93584632,"total":559874528},
                "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/2714b5519c6c3516b1000e7c5e1eba998dfe1fe8/gemma-4-E4B-it-Q4_K_M.gguf":{"done":0,"total":5335289824}
            }}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (done, total) = LlamaManager.SumProgress(doc.RootElement);

        Assert.Equal(93584632L, done);
        Assert.Equal(559874528L + 5335289824L, total);
    }

    [Fact]
    public void SumProgress_Returns_Zero_When_Progress_Missing()
    {
        using var doc = JsonDocument.Parse("{}");
        var (done, total) = LlamaManager.SumProgress(doc.RootElement);

        Assert.Equal(0, done);
        Assert.Equal(0, total);
    }

    [Fact]
    public void ModelDownloadProgress_Fraction_Computes_Correctly()
    {
        var p = new ModelDownloadProgress(
            Model: "a/b",
            DownloadedBytes: 250,
            TotalBytes: 1000,
            Done: false,
            Failed: false);

        Assert.Equal(0.25, p.Fraction, precision: 5);
    }

    [Fact]
    public void ModelDownloadProgress_Fraction_Is_Zero_When_Total_Unknown()
    {
        var p = new ModelDownloadProgress(
            Model: "a/b",
            DownloadedBytes: 100,
            TotalBytes: 0,
            Done: false,
            Failed: false);

        Assert.Equal(0.0, p.Fraction);
    }

    // ----- helpers -----------------------------------------------------------

    private static async Task<List<(string Event, string Model, JsonElement Data)>> CollectAsync(StreamReader reader)
    {
        var list = new List<(string, string, JsonElement)>();
        await foreach (var tuple in LlamaManager.ParseSseStreamAsync(reader, CancellationToken.None))
            list.Add(tuple);
        return list;
    }
}
