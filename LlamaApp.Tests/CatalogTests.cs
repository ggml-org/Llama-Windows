using System.Text.Json;
using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the Hugging Face <see cref="Catalog"/> parsing layer: the
/// family → size → build flattening into <see cref="Repository"/> records and
/// the pure helpers (byte formatting, quant extraction from GGUF filenames).
/// </summary>
public class CatalogTests
{
    // ----- Flatten ----------------------------------------------------------

    [Fact]
    public void Flatten_Produces_One_Repository_Per_Build()
    {
        var families = new[]
        {
            new Catalog.CatalogFamily
            {
                Name = "GPT-OSS",
                Brand = "OpenAI",
                Description = "Open-weight model",
                License = "Apache-2.0",
                Featured = true,
                Sizes =
                [
                    new Catalog.CatalogSize
                    {
                        Name = "GPT-OSS 20B",
                        Params = "20B",
                        Vision = false,
                        Builds =
                        [
                            new Catalog.CatalogBuild { Quant = "mxfp4", Size = "12.1 GB", SizeBytes = 12992276025UL, Repo = "ggml-org/gpt-oss-20b-GGUF" },
                            new Catalog.CatalogBuild { Quant = "Q4_0", Size = "11 GB", SizeBytes = 11811160064UL, Repo = "ggml-org/gpt-oss-20b-GGUF" },
                        ],
                    },
                    new Catalog.CatalogSize
                    {
                        Name = "GPT-OSS 120B",
                        Params = "120B",
                        Vision = true,
                        Builds =
                        [
                            new Catalog.CatalogBuild { Quant = "mxfp4", Size = "60 GB", SizeBytes = 64424509440UL, Repo = "ggml-org/gpt-oss-120b-GGUF" },
                        ],
                    },
                ],
            },
        };

        var repos = Catalog.Flatten(families);

        Assert.Equal(3, repos.Count);
    }

    [Fact]
    public void Flatten_Maps_Family_Size_And_Build_Fields()
    {
        var families = new[]
        {
            new Catalog.CatalogFamily
            {
                Name = "GPT-OSS",
                Brand = "OpenAI",
                Description = "Open-weight model",
                License = "Apache-2.0",
                Featured = true,
                Sizes =
                [
                    new Catalog.CatalogSize
                    {
                        Name = "GPT-OSS 20B",
                        Params = "20B",
                        Vision = true,
                        Builds =
                        [
                            new Catalog.CatalogBuild { Quant = "mxfp4", Size = "12.1 GB", SizeBytes = 12992276025UL, Repo = "ggml-org/gpt-oss-20b-GGUF" },
                        ],
                    },
                ],
            },
        };

        var repo = Assert.Single(Catalog.Flatten(families));

        Assert.Equal("ggml-org/gpt-oss-20b-GGUF", repo.Name);       // build.Repo
        Assert.Equal("mxfp4", repo.Quant);                          // build.Quant
        Assert.Equal("12.1 GB", repo.Size);                         // build.Size
        Assert.Equal(12992276025UL, repo.SizeBytes);                // build.SizeBytes
        Assert.Equal("GPT-OSS 20B", repo.DisplayName);              // size.Name
        Assert.Equal("20B", repo.Parameters);                       // size.Params
        Assert.True(repo.Vision);                                   // size.Vision
        Assert.Equal("OpenAI", repo.Brand);                         // family.Brand
        Assert.Equal("Open-weight model", repo.Description);        // family.Description
        Assert.Equal("Apache-2.0", repo.License);                   // family.License
        Assert.True(repo.Featured);                                 // family.Featured
    }

    [Fact]
    public void Flatten_Empty_Input_Returns_Empty_List()
    {
        Assert.Empty(Catalog.Flatten([]));
    }

    [Fact]
    public void Catalog_Json_Deserializes_And_Flattens()
    {
        // End-to-end: the catalog.json schema → DTOs → flat repositories.
        const string json = """
        [
          {
            "name": "Gemma 3",
            "brand": "Google",
            "description": "Multimodal model",
            "details": "...",
            "released": "2025-03",
            "license": "Gemma",
            "featured": false,
            "sizes": [
              {
                "name": "Gemma 3 4B",
                "params": "4B",
                "vision": true,
                "builds": [
                  { "quant": "Q4_K_M", "size": "2.5 GB", "sizeBytes": 2526080992, "repo": "ggml-org/gemma-3-4b-it-GGUF" }
                ]
              }
            ]
          }
        ]
        """;

        var families = JsonSerializer.Deserialize<Catalog.CatalogFamily[]>(json);
        var repos = Catalog.Flatten(families!);

        var repo = Assert.Single(repos);
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF", repo.Name);
        Assert.Equal("Q4_K_M", repo.Quant);
        Assert.Equal("Gemma 3 4B", repo.DisplayName);
        Assert.Equal("Google", repo.Brand);
        Assert.True(repo.Vision);
        Assert.False(repo.Featured);
        // repo:quant is the id form POST /models/load requires.
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M", repo.ServerModelId);
    }

    // ----- FormatBytes -------------------------------------------------------

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(512UL, "512 B")]
    [InlineData(1024UL, "1 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(1048576UL, "1 MB")]
    [InlineData(2526080992UL, "2.4 GB")]
    [InlineData(1073741824UL, "1 GB")]
    [InlineData(1099511627776UL, "1 TB")]
    public void FormatBytes_Formats_Human_Readable_Sizes(ulong bytes, string expected)
    {
        Assert.Equal(expected, Catalog.FormatBytes(bytes));
    }

    // ----- ExtractQuant ------------------------------------------------------

    [Theory]
    [InlineData("gemma-3-4b-it-Q4_K_M", "Q4_K_M")]
    [InlineData("gpt-oss-20b-mxfp4", "mxfp4")]
    [InlineData("Q4_0", "Q4_0")]
    public void ExtractQuant_Finds_Quant_Label_In_File_Name(string fileName, string expected)
    {
        Assert.Equal(expected, Catalog.ExtractQuant(fileName));
    }

    [Theory]
    [InlineData("model-q4_k_m")] // lowercase 'q' is not matched
    [InlineData("a-Q")]          // single-char segment is not a quant
    [InlineData("gemma-3-4b-it")]
    public void ExtractQuant_Returns_Null_When_No_Quant_Segment(string fileName)
    {
        Assert.Null(Catalog.ExtractQuant(fileName));
    }
}
