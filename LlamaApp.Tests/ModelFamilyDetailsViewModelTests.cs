using LlamaApp.HuggingFace;
using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the family details ViewModel
/// (<see cref="ModelFamilyDetailsViewModel"/>): the size → variant →
/// download hierarchy. Sizes select in catalog order, variants rebuild per
/// size, installed variants are marked and can't be re-downloaded, and the
/// download delegates to the shell's existing workflow via
/// <see cref="IModelFamilyDetailsHost"/>. The host is faked — no server,
/// no disk. Families use a brand outside the logo map so no XAML logo
/// pipeline runs.
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
    public void Downloadable_Variants_Carry_No_State_Caption()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost());

        Assert.Equal("", vm.Variants[0].StateCaption);
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

    // ---- Fit evaluation ----

    /// <summary>Probe that fits builds below a size threshold — the rest
    /// get a "won't fit" verdict and its note.</summary>
    private static Func<ModelFamilySize, ModelFamilyBuild, CancellationToken, Task<VariantFitVerdict>>
        FitBelow(ulong thresholdBytes) =>
        (_, build, _) => Task.FromResult(new VariantFitVerdict(
            build.SizeBytes < thresholdBytes ? HardwareFitLevel.Fit : HardwareFitLevel.NoWay,
            build.SizeBytes < thresholdBytes ? null : "Won't fit: needs about 99 GB."));

    [Fact]
    public async Task Unfit_Variants_Tint_Red_And_Carry_The_Fit_Note()
    {
        // Only the 2.5 GB build fits: the 4.9 GB one must turn red (but
        // stay tappable — the preflight flyout owns the explanation).
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost(),
            variantFitProbe: FitBelow(4_000_000_000UL));
        await vm.FitEvaluation;

        var (fits, unfit) = (vm.Variants[0], vm.Variants[1]);
        Assert.Equal(HardwareFitLevel.Fit, fits.FitLevel);
        Assert.Null(fits.RowTooltip); // no note from the probe → no tooltip

        Assert.Equal(HardwareFitLevel.NoWay, unfit.FitLevel);
        Assert.Equal("Won't fit: needs about 99 GB.", unfit.RowTooltip);
        Assert.True(unfit.IsSelectable); // red, not disabled
        // The color shows the level on screen; the accessible name words it.
        Assert.Contains("won't fit", unfit.AccessibleName);
    }

    [Fact]
    public async Task Variant_Rows_Tint_Green_Yellow_Red_By_Level()
    {
        // One build per level: the row's level must map through to the
        // badge tint (the badges themselves are XAML objects and never
        // instantiate here — the color mapping is covered in HardwareFitPresentationTests).
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost(),
            variantFitProbe: (size, build, _) => Task.FromResult(new VariantFitVerdict(
                build == Q4 ? HardwareFitLevel.Fit
                : build == Q8 ? HardwareFitLevel.Might
                : HardwareFitLevel.NoWay,
                "note")));
        await vm.FitEvaluation;

        Assert.Equal(HardwareFitLevel.Fit, vm.Variants[0].FitLevel);
        Assert.Equal(HardwareFitLevel.Might, vm.Variants[1].FitLevel);
        Assert.Contains("might fit", vm.Variants[1].AccessibleName);

        vm.SelectSize(vm.Sizes[1]);
        var red = Assert.Single(vm.Variants);
        Assert.Equal(HardwareFitLevel.NoWay, red.FitLevel);
    }

    [Fact]
    public async Task Size_Cells_Dim_Only_When_No_Build_Fits()
    {
        // 4B has a fitting build (stays lit); 12B's only build (8.1 GB)
        // doesn't — the cell dims and its tooltip carries the note.
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost(),
            variantFitProbe: FitBelow(4_000_000_000UL));
        await vm.FitEvaluation;

        Assert.True(vm.Sizes[0].FitsOnDevice);
        Assert.Equal("Gemma 3 4B", vm.Sizes[0].CellTooltip);

        Assert.False(vm.Sizes[1].FitsOnDevice);
        Assert.Equal(0.4, vm.Sizes[1].CellOpacity);
        Assert.StartsWith("Won't fit", vm.Sizes[1].CellTooltip);
    }

    [Fact]
    public async Task Verdicts_Survive_A_Size_Switch()
    {
        // Rows rebuild from scratch on a size switch — the cached verdict
        // must re-apply instantly, without waiting for another probe pass.
        var probeCalls = 0;
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost(),
            variantFitProbe: (size, build, _) =>
            {
                probeCalls++;
                return Task.FromResult(new VariantFitVerdict(
                    build.SizeBytes < 4_000_000_000UL ? HardwareFitLevel.Fit : HardwareFitLevel.NoWay,
                    "note"));
            });
        await vm.FitEvaluation;

        vm.SelectSize(vm.Sizes[1]); // 12B

        var variant = Assert.Single(vm.Variants);
        Assert.Equal(HardwareFitLevel.NoWay, variant.FitLevel);
        Assert.Equal("note", variant.FitNote);
        Assert.Equal(3, probeCalls); // 3 builds total, no second pass
    }

    [Fact]
    public async Task Failed_Probes_Never_Tint()
    {
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost(),
            variantFitProbe: (_, _, _) => throw new InvalidOperationException("probe died"));
        await vm.FitEvaluation;

        Assert.All(vm.Variants, v => Assert.Null(v.FitLevel));
        Assert.All(vm.Sizes, s => Assert.True(s.FitsOnDevice));
    }

    [Fact]
    public async Task Installed_Variants_Are_Not_Tinted_By_Fit_Verdicts()
    {
        // What's on disk is on disk — an installed build keeps its neutral
        // row even when the probe says it wouldn't fit now.
        var host = new FakeHost();
        host.InstalledIds.Add("ggml-org/gemma-3-4b-it-GGUF:Q8_0");
        var vm = new ModelFamilyDetailsViewModel(Gemma(), host,
            variantFitProbe: FitBelow(1UL)); // nothing fits
        await vm.FitEvaluation;

        Assert.Equal(HardwareFitLevel.NoWay, vm.Variants[0].FitLevel); // Q4 red
        Assert.True(vm.Variants[1].IsInstalled);
        Assert.Null(vm.Variants[1].FitLevel); // installed stays untinted
    }

    [Fact]
    public async Task Dispose_Cancels_The_Fit_Evaluation()
    {
        var gate = new TaskCompletionSource<VariantFitVerdict>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new ModelFamilyDetailsViewModel(Gemma(), new FakeHost(),
            variantFitProbe: (_, _, _) => gate.Task);

        vm.Dispose();
        gate.SetResult(new VariantFitVerdict(HardwareFitLevel.NoWay, "too big"));
        await vm.FitEvaluation;

        // The verdict arrived after cancellation — nothing was applied.
        Assert.All(vm.Variants, v => Assert.Null(v.FitLevel));
        Assert.All(vm.Sizes, s => Assert.True(s.FitsOnDevice));
    }
}
