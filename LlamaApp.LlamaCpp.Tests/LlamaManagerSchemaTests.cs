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
    public void Map_Downloading_Model_From_Dto()
    {
        // Real /models shape for a mid-download model: the id is the bare repo
        // (the quant is resolved only on completion) and the status value is
        // "downloading" — neither loading nor loaded.
        var dto = new LlamaManager.ServerModelDto
        {
            Id = "mistralai/Ministral-3-3B-Instruct-2512-GGUF",
            Status = new LlamaManager.ModelStatusDto { Value = "downloading" },
            Architecture = new LlamaManager.ArchitectureDto { InputModalities = ["text"] },
            Source = "cache",
            CanRemove = true,
        };

        var model = LlamaManager.Map(dto);

        Assert.True(model.IsDownloading);
        Assert.False(model.IsLoading);
        Assert.False(model.IsLoaded);
    }

    [Fact]
    public void Map_Null_Status_And_Architecture_Default_To_Unloaded()
    {
        var model = LlamaManager.Map(new LlamaManager.ServerModelDto());

        Assert.Equal("", model.Id);
        Assert.Equal("", model.Status);
        Assert.False(model.IsLoaded);
        Assert.False(model.IsLoading);
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
    public async Task ParseSseStream_Dispatches_Multiple_Events_On_Blank_Lines()
    {
        const string payload = """
            data: {"model":"a/b","event":"download_started","data":{}}

            data: {"model":"a/b","event":"download_finished","data":{}}

            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Equal(2, events.Count);
        Assert.Equal("download_started", events[0].Event);
        Assert.Equal("download_finished", events[1].Event);
    }

    [Fact]
    public async Task ParseSseStream_Flushes_Trailing_Event_At_Eof()
    {
        // No trailing blank line — a server that drops the connection
        // mid-stream must not silently lose the last event.
        const string payload = "data: {\"model\":\"a/b\",\"event\":\"download_finished\",\"data\":{}}";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Single(events);
        Assert.Equal("download_finished", events[0].Event);
    }

    [Fact]
    public async Task ParseSseStream_Handles_Crlf_Line_Endings()
    {
        const string payload = "data: {\"model\":\"a/b\",\"event\":\"download_finished\",\"data\":{}}\r\n\r\n";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Single(events);
        Assert.Equal("download_finished", events[0].Event);
    }

    [Fact]
    public async Task ParseSseStream_Joins_Multi_Line_Data()
    {
        // SSE allows one event's data to span several data: lines; they are
        // joined with '\n' before parsing.
        const string payload = """
            data: {"model":"a/b","event":"download_progress",
            data: "data":{}}

            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Single(events);
        Assert.Equal("download_progress", events[0].Event);
        Assert.Equal("a/b", events[0].Model);
    }

    [Fact]
    public async Task ParseSseStream_Skips_Payload_Without_Event_Field()
    {
        const string payload = """
            data: {"model":"a/b","data":{}}

            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream);

        var events = await CollectAsync(reader);

        Assert.Empty(events);
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
    public void SumProgress_Skips_Non_Object_Entries_And_Missing_Fields()
    {
        const string dataJson = """
            {"progress":{
                "url1":{"done":10},
                "url2":"not-an-object",
                "url3":{"total":100},
                "url4":{"done":"oops","total":50}
            }}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (done, total) = LlamaManager.SumProgress(doc.RootElement);

        Assert.Equal(10, done);
        Assert.Equal(150, total);
    }

    // ----- status_change data payload (load progress) ----------------------

    [Fact]
    public void ParseStatusChange_Loading_Reports_Value_As_Fraction()
    {
        // Real payload captured from /models/sse while loading. A single-stage
        // load reports the stage's value directly as the overall fraction.
        const string dataJson = """
            {"status":"loading","progress":{"stages":["text_model"],"current":"text_model","value":0.9664499163627625}}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (status, fraction) = LlamaManager.ParseStatusChange(doc.RootElement);

        Assert.Equal("loading", status);
        Assert.Equal(0.9664499163627625, fraction, precision: 6);
    }

    [Fact]
    public void ParseStatusChange_Loaded_Carries_No_Progress()
    {
        // Real payload captured from /models/sse on completion (the info body
        // is trimmed) — no progress object, so the fraction stays 0; callers
        // report the terminal 1.0 themselves.
        const string dataJson = """
            {"status":"loaded","info":{"id":"ggml-org/gemma-3-1b-it-qat-GGUF:Q4_0"}}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (status, fraction) = LlamaManager.ParseStatusChange(doc.RootElement);

        Assert.Equal("loaded", status);
        Assert.Equal(0, fraction);
    }

    [Fact]
    public void ParseStatusChange_MultiStage_Weights_Value_By_Stage_Position()
    {
        // Second of two stages at 50% -> (1 + 0.5) / 2 = 0.75 overall.
        const string dataJson = """
            {"status":"loading","progress":{"stages":["text_model","mmproj"],"current":"mmproj","value":0.5}}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (status, fraction) = LlamaManager.ParseStatusChange(doc.RootElement);

        Assert.Equal("loading", status);
        Assert.Equal(0.75, fraction, precision: 6);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"status\":42}")]
    public void ParseStatusChange_Missing_Or_Non_String_Status_Yields_Empty(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var (status, fraction) = LlamaManager.ParseStatusChange(doc.RootElement);

        Assert.Equal("", status);
        Assert.Equal(0, fraction);
    }

    [Fact]
    public void ParseStatusChange_Non_Number_Value_Yields_Zero_Fraction()
    {
        // Regression: TryGetDouble throws on a String element (same as
        // TryGetInt64) — the ValueKind guard must run before it.
        const string dataJson = """
            {"status":"loading","progress":{"value":"oops"}}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (status, fraction) = LlamaManager.ParseStatusChange(doc.RootElement);

        Assert.Equal("loading", status);
        Assert.Equal(0, fraction);
    }

    [Fact]
    public void ParseStatusChange_Clamps_Out_Of_Range_Value()
    {
        const string dataJson = """
            {"status":"loading","progress":{"stages":["text_model"],"current":"text_model","value":1.7}}
            """;

        using var doc = JsonDocument.Parse(dataJson);
        var (_, fraction) = LlamaManager.ParseStatusChange(doc.RootElement);

        Assert.Equal(1.0, fraction);
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
