using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="HubClient.ParseModels"/>: the /api/models JSON
/// array → <see cref="HubClient.HubSearchResult"/> mapping — id/downloads/
/// likes extraction, missing-field tolerance, and malformed-JSON resilience.
/// </summary>
public class HubSearchTests
{
    // A realistic /api/models?filter=gguf response — extra per-entry payload
    // (siblings, tags, config, …) must be ignored, only id/downloads/likes mapped.
    private const string ModelsJson = """
        [
          {
            "_id": "65f1a2b3c4d5e6f7a8b9c0d1",
            "id": "ggml-org/gemma-3-4b-it-GGUF",
            "likes": 312,
            "downloads": 1234567,
            "pipeline_tag": "text-generation",
            "library_name": "gguf",
            "tags": [ "gguf", "gemma3" ],
            "siblings": [ { "rfilename": "gemma-3-4b-it-Q4_0.gguf" } ]
          },
          { "id": "unsloth/Qwen3-0.6B-GGUF" },
          { "id": "org/repo", "downloads": 0, "likes": 0 },
          { "downloads": 42 }
        ]
        """;

    [Fact]
    public void Parses_ids_downloads_and_likes()
    {
        var results = HubClient.ParseModels(ModelsJson);

        Assert.Equal(4, results.Count);
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF", results[0].Id);
        Assert.Equal(1234567, results[0].Downloads);
        Assert.Equal(312, results[0].Likes);
    }

    [Fact]
    public void Missing_fields_map_to_zero()
    {
        var results = HubClient.ParseModels(ModelsJson);

        Assert.Equal(0, results[1].Downloads);
        Assert.Equal(0, results[1].Likes);
        Assert.Equal(0, results[2].Downloads);
        Assert.Equal("", results[3].Id); // entry without an id
        Assert.Equal(42, results[3].Downloads);
    }

    [Fact]
    public void Malformed_json_yields_empty_list()
    {
        Assert.Empty(HubClient.ParseModels("not json"));
        Assert.Empty(HubClient.ParseModels(""));
    }
}
