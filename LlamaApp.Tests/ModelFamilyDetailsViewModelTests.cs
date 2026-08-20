using LlamaApp.HuggingFace;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the family details ViewModel
/// (<see cref="ModelFamilyDetailsViewModel"/>): the size → variant →
/// download hierarchy. Sizes select in catalog order, variants rebuild per
/// size with the catalog's first build flagged "Default", installed variants
/// are marked and can't be re-downloaded, and the download delegates to the
/// shell's existing workflow via <see cref="IModelFamilyDetailsHost"/>. The
/// host is faked — no server, no disk. Families use a brand outside the logo
/// map so no XAML logo pipeline runs.
/// </summary>
public sealed class ModelFamilyDetailsViewModelTests
{
    private sealed class FakeHost : IModelFamilyDetailsHost
    {
        public HashSet<string> InstalledIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build)> Downloads { get; } = [];
        public int CloseDetailsCalls;

        bool IModelFamilyDetailsHost.IsVariantInstalled(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build)
            => InstalledIds.Contains(build.Repo + ":" + build.Quant);

        void IModelFamilyDetailsHost.DownloadVariant(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build)
            => Downloads.Add((family, size, build));

        void IModelFamilyDetailsHost.CloseDetails() => CloseDetailsCalls++;
    }

    private static readonly ModelFamilyBuild Q4 = new()
    {
        Quant = "Q4_K_M", Size = "2.5 GB", SizeBytes = 2_526_080_992UL,
        Repo = "ggml-org/gemma-3-4b-it-GGUF",
    };

    private static readonly ModelFamilyBuild Q8 = new()
    {
        Quant = "Q8_0", Size = "4.9 GB", SizeBytes = 4_900_000_000UL,
        Repo = "ggml-org/gemma-3-4b-it-GGUF",
    };

    private static readonly ModelFamilyBuild BigQ4 = new()
    {
        Quant = "Q4_K_M", Size = "8.1 GB", SizeBytes = 8_118_434_880UL,
        Repo = "ggml-org/gemma-3-12b-it-GGUF",
    };

    private static ModelFamily Gemma() => new()
    {
        Name = "Gemma 3",
        Brand = "TestBrand", // outside the logo map — no SVG rasterization
        Description = "Google's multimodal model family.",
        License = "Gemma",
        Featured = true,
        Sizes = new[]
        {
            new ModelFamilySize { Name = "Gemma 3 4B", Params = "4B", Builds = new[] { Q4, Q8 } },
            new ModelFamilySize { Name = "Gemma 3 12B", Params = "12B", Builds = new[] { BigQ4 } },
        },
    };

    [Fact]
    public void First_Size_Is_Preselected_And_Its_Variants_Listed()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());

        Assert.Equal("4B", vm.SelectedSize?.Label);
        Assert.Equal(2, vm.Variants.Count);
        Assert.Equal("Q4_K_M", vm.Variants[0].Build.Quant);
        Assert.Equal("Q8_0", vm.Variants[1].Build.Quant);
    }

    [Fact]
    public void Catalogs_First_Build_Is_Flagged_Default()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());

        Assert.True(vm.Variants[0].IsDefault);
        Assert.False(vm.Variants[1].IsDefault);
        Assert.Equal("Default", vm.Variants[0].StateCaption);
        Assert.Equal("", vm.Variants[1].StateCaption);
    }

    [Fact]
    public void Variants_Lead_With_The_Friendly_Name_And_Carry_The_Raw_Detail()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());

        Assert.Equal("Balanced", vm.Variants[0].FriendlyName);
        Assert.Equal("Q4_K_M · 2.5 GB", vm.Variants[0].DetailText);
        Assert.Equal("Higher quality", vm.Variants[1].FriendlyName);
    }

    [Fact]
    public void Selecting_A_Size_Rebuilds_The_Variants()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());

        vm.SelectSize(vm.Sizes[1]);

        Assert.Equal("12B", vm.SelectedSize?.Label);
        Assert.True(vm.Sizes[1].IsSelected);
        Assert.False(vm.Sizes[0].IsSelected);
        var variant = Assert.Single(vm.Variants);
        Assert.Equal("ggml-org/gemma-3-12b-it-GGUF", variant.Build.Repo);
    }

    [Fact]
    public void Selecting_A_Foreign_Size_Option_Is_A_No_Op()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());
        var foreign = new ModelFamilySizeOption(new ModelFamilySize { Params = "999B" });

        vm.SelectSize(foreign);

        Assert.Equal("4B", vm.SelectedSize?.Label);
    }

    [Fact]
    public void Installed_Variants_Are_Marked_And_Not_Selectable()
    {
        var host = new FakeHost();
        host.InstalledIds.Add("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M");
        var vm = new ModelFamilyDetailsViewModel(Gemma(), host);

        Assert.True(vm.Variants[0].IsInstalled);
        Assert.False(vm.Variants[0].IsSelectable);
        Assert.Equal("Installed", vm.Variants[0].StateCaption);
        Assert.False(vm.Variants[1].IsInstalled);
        Assert.True(vm.Variants[1].IsSelectable);
    }

    [Fact]
    public void Download_Delegates_To_The_Host_With_The_Selected_Size()
    {
        var host = new FakeHost();
        var vm = new ModelFamilyDetailsViewModel(Gemma(), host);

        vm.DownloadVariant(vm.Variants[1]);

        var (family, size, build) = Assert.Single(host.Downloads);
        Assert.Equal("Gemma 3", family.Name);
        Assert.Equal("4B", size.Params);
        Assert.Same(Q8, build);
    }

    [Fact]
    public void Download_Of_An_Installed_Variant_Is_A_No_Op()
    {
        var host = new FakeHost();
        host.InstalledIds.Add("ggml-org/gemma-3-4b-it-GGUF:Q4_K_M");
        var vm = new ModelFamilyDetailsViewModel(Gemma(), host);

        vm.DownloadVariant(vm.Variants[0]);

        Assert.Empty(host.Downloads);
    }

    [Fact]
    public void Technical_Details_Come_From_The_Family()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());

        Assert.Equal("TestBrand", vm.Provider);
        Assert.Equal("Gemma", vm.License);
        Assert.Equal("GGUF", vm.Format);
    }
}
