using LlamaApp.HuggingFace;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the browse-section rules: family filtering
/// (<see cref="RecommendedFiltering.FilterFamiliesForDisplay"/>), the
/// flattening of public <see cref="ModelFamily"/> objects
/// (<see cref="Catalog.Flatten(System.Collections.Generic.IReadOnlyList{ModelFamily})"/>),
/// and the family row presentation (<see cref="ModelFamilyViewModel"/>).
/// The family view-models are built without a logo resolver so no XAML
/// objects are created.
/// </summary>
public class ModelFamilyTests
{
    private static ModelFamily Family(
        string name, bool featured = false, params ModelFamilySize[] sizes) => new()
    {
        Name = name,
        Brand = "TestBrand",
        Description = "Test family",
        License = "Apache-2.0",
        Featured = featured,
        Sizes = sizes,
    };

    private static ModelFamilySize Size(
        string name, string parameters, params ModelFamilyBuild[] builds) => new()
    {
        Name = name,
        Params = parameters,
        Builds = builds,
    };

    private static ModelFamilyBuild Build(string quant, string size, ulong sizeBytes, string repo) => new()
    {
        Quant = quant,
        Size = size,
        SizeBytes = sizeBytes,
        Repo = repo,
    };

    // ----- FilterFamiliesForDisplay ------------------------------------------

    [Fact]
    public void Only_Featured_Families_Get_A_Browse_Row()
    {
        var filtered = RecommendedFiltering.FilterFamiliesForDisplay(new[]
        {
            Family("a"),
            Family("b", featured: true),
            Family("c"),
        });

        Assert.Equal(new[] { "b" }, filtered.Select(f => f.Name));
    }

    [Fact]
    public void Family_Catalog_Order_Is_Preserved()
    {
        var filtered = RecommendedFiltering.FilterFamiliesForDisplay(new[]
        {
            Family("a", featured: true),
            Family("b"),
            Family("c", featured: true),
        });

        Assert.Equal(new[] { "a", "c" }, filtered.Select(f => f.Name));
    }

    [Fact]
    public void No_Featured_Families_Yields_Empty_Browse_Section()
    {
        Assert.Empty(RecommendedFiltering.FilterFamiliesForDisplay(new[] { Family("a") }));
    }

    // ----- Flatten(IReadOnlyList<ModelFamily>) --------------------------------

    [Fact]
    public void Flatten_Of_Public_Families_Matches_The_Dto_Flatten()
    {
        var families = new[]
        {
            Family("Gemma 3", featured: true,
                Size("Gemma 3 4B", "4B",
                    Build("Q4_K_M", "2.5 GB", 2_526_080_992UL, "ggml-org/gemma-3-4b-it-GGUF"),
                    Build("Q8_0", "4.9 GB", 4_900_000_000UL, "ggml-org/gemma-3-4b-it-GGUF")),
                Size("Gemma 3 12B", "12B",
                    Build("Q4_K_M", "8.1 GB", 8_118_434_880UL, "ggml-org/gemma-3-12b-it-GGUF"))),
        };

        var repos = Catalog.Flatten((IReadOnlyList<ModelFamily>)families);

        Assert.Equal(3, repos.Count);
        var first = repos[0];
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF", first.Name);
        Assert.Equal("Q4_K_M", first.Quant);
        Assert.Equal("2.5 GB", first.Size);
        Assert.Equal(2_526_080_992UL, first.SizeBytes);
        Assert.Equal("Gemma 3 4B", first.DisplayName);
        Assert.Equal("4B", first.Parameters);
        Assert.Equal("TestBrand", first.Brand);
        Assert.Equal("Test family", first.Description);
        Assert.Equal("Apache-2.0", first.License);
        Assert.True(first.Featured);
        // repo:quant is the id form POST /models/load requires.
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M", first.ServerModelId);
    }

    // ----- ModelFamilyViewModel (the row presentation) ------------------------

    [Fact]
    public void DisplayParameterSizes_Joins_With_Middle_Dots()
    {
        var vm = new ModelFamilyViewModel(Family("Gemma 3", featured: false,
            Size("Gemma 3 1B", "1B"), Size("Gemma 3 4B", "4B"), Size("Gemma 3 12B", "12B")));

        Assert.Equal("1B · 4B · 12B", vm.DisplayParameterSizes);
    }

    [Fact]
    public void DisplayParameterSizes_Caps_At_Five_And_Marks_The_Cut()
    {
        var vm = new ModelFamilyViewModel(Family("Big", featured: false,
            Size("S1", "1B"), Size("S2", "2B"), Size("S3", "3B"),
            Size("S4", "4B"), Size("S5", "5B"), Size("S6", "6B"), Size("S7", "7B")));

        Assert.Equal("1B · 2B · 3B · 4B · 5B · …", vm.DisplayParameterSizes);
    }

    [Fact]
    public void DisplayParameterSizes_Skips_Blank_And_Five_Is_Not_Capped()
    {
        var vm = new ModelFamilyViewModel(Family("Exact", featured: false,
            Size("S1", "1B"), Size("S2", ""), Size("S3", "3B"),
            Size("S4", "4B"), Size("S5", "5B"), Size("S6", "6B")));

        // The blank size is dropped; the remaining five fit without a cut.
        Assert.Equal("1B · 3B · 4B · 5B · 6B", vm.DisplayParameterSizes);
    }

    [Fact]
    public void AccessibleName_Carries_Name_Sizes_And_Affordance()
    {
        var vm = new ModelFamilyViewModel(Family("Gemma 3", featured: false,
            Size("Gemma 3 1B", "1B"), Size("Gemma 3 4B", "4B")));

        Assert.Equal("Gemma 3. Available sizes: 1B, 4B. Open details.", vm.AccessibleName);
    }
}
