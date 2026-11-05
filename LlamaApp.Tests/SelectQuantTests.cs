using LlamaApp.HuggingFace;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the pure <c>llama://install</c> quant choice
/// (<see cref="QuantSelection.SelectQuant"/>) and the build derivation it
/// works on (<see cref="QuantSelection.BuildsFor"/>): model-card links prefer
/// a fitting Q4_K_M/Q4_0, then the largest fitting build, while explicit
/// file-link quants bypass fit and are preserved all the way into repo:quant.
/// Tests also pin refusal, conservative/deterministic fallbacks, exact HF
/// labels, and the sidecar/shard handling that feeds selection.
/// </summary>
public sealed class SelectQuantTests
{
    /// <summary>A 10 GB budget: Fit ≤ 9 GB, Might ≤ 12.5 GB (the
    /// HardwareFitHeuristic bands), past that NoWay.</summary>
    private const ulong Budget = 10_000_000_000;

    private static Repository Build(string quant, ulong sizeBytes) => new()
    {
        Name = "org/repo",
        Quant = quant,
        Description = "",
        License = "",
        Parameters = "",
        Size = "",
        SizeBytes = sizeBytes,
        Vision = false,
    };

    // ---- model-card default / largest-fitting ----

    [Fact]
    public void SelectQuant_ModelCard_PrefersRecommendedQ4KM_OverLargerFitQuants()
    {
        var builds = new List<Repository>
        {
            Build("Q2_K", 2_000_000_000),   // Fit
            Build("Q4_K_M", 5_000_000_000), // Fit — recommended default
            Build("Q8_0", 8_000_000_000),   // Fit and larger, but not the default
            Build("BF16", 9_000_000_000),   // Must not win merely by sorting first/largest
        };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("Q4_K_M", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_ModelCard_UsesQ4ZeroAsTheSecondRecommendedChoice()
    {
        var builds = new List<Repository>
        {
            Build("Q4_0", 3_000_000_000),
            Build("Q8_0", 8_000_000_000),
        };

        Assert.Equal("Q4_0", QuantSelection.SelectQuant(builds, Budget)?.Quant);
    }

    [Fact]
    public void SelectQuant_ModelCardWithoutRecommendedQuant_PicksTheLargestThatFits()
    {
        var builds = new List<Repository>
        {
            Build("Q2_K", 2_000_000_000),
            Build("Q5_K_M", 5_000_000_000),
            Build("Q8_0", 12_000_000_000),  // Might — acceptable, and largest
            Build("F16", 20_000_000_000),   // NoWay — never picked
        };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("Q8_0", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_SingleNoWayBuild_Refuses()
    {
        // One build, comfortably past the optimistic band on a KNOWN budget:
        // the machine can't run it, so the selector refuses instead of
        // silently downloading it.
        var builds = new List<Repository> { Build("F16", 20_000_000_000) };

        Assert.Null(QuantSelection.SelectQuant(builds, Budget));
    }

    [Fact]
    public void SelectQuant_TiesBreakDeterministically()
    {
        var builds = new List<Repository>
        {
            Build("Q5_0", 4_000_000_000),
            Build("Q3_K_M", 4_000_000_000),
        };

        var first = QuantSelection.SelectQuant(builds, Budget);
        var second = QuantSelection.SelectQuant(builds, Budget);

        // Same size: quant tag ordinal decides — always the same answer.
        Assert.Equal("Q3_K_M", first?.Quant);
        Assert.Equal(first, second);
    }

    // ---- none-fits fallback ----

    [Fact]
    public void SelectQuant_WhenNoneFits_OnAKnownBudget_Refuses()
    {
        // Budget of 1 GB, and every build is NoWay: nothing is acceptable, so
        // the selector refuses — a remote link must not start a download the
        // machine can't run.
        var builds = new List<Repository>
        {
            Build("Q2_K", 2_000_000_000),
            Build("Q4_K_M", 4_000_000_000),
            Build("Q6_K", 6_000_000_000),
        };

        Assert.Null(QuantSelection.SelectQuant(builds, 1_000_000_000));
    }

    [Fact]
    public void SelectQuant_WhenNoneFits_WithoutConservativeTag_Refuses()
    {
        // No Q4_K_M/Q4_0 in the repo, everything NoWay: still refuse — the
        // conservative fallback is for an UNKNOWN budget, not for a known
        // budget where nothing fits.
        var builds = new List<Repository>
        {
            Build("Q6_K", 6_000_000_000),
            Build("Q5_K_S", 5_000_000_000),
        };

        Assert.Null(QuantSelection.SelectQuant(builds, 1_000_000_000));
    }

    // ---- unknown-sizes default ----

    [Fact]
    public void SelectQuant_WhenSizesAreUnknown_DefaultsToTheConservativeKnownQuant()
    {
        // Sizes all unknown (the Hub omitted them): nothing can be fit-ranked,
        // so the default is the conservative known quant the repo ships.
        var builds = new List<Repository>
        {
            Build("F16", 0),
            Build("Q8_0", 0),
            Build("Q4_K_M", 0),
        };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("Q4_K_M", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_WhenSizesAreUnknown_AndNoConservativeTag_PicksDeterministically()
    {
        // Nothing to rank and no conservative tag: the selector still names a
        // build deterministically instead of returning null (the caller must
        // not request repo:Q4_K_M blindly when the repo doesn't ship it).
        var builds = new List<Repository>
        {
            Build("F16", 0),
            Build("Q8_0", 0),
        };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("F16", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_MeasurableBuilds_OutrankUnknownSizeOnes()
    {
        // A size we know beats a size we don't — even a conservative-tagged
        // unknown can't claim "largest-fitting".
        var builds = new List<Repository>
        {
            Build("Q4_K_M", 0),
            Build("Q2_K", 3_000_000_000),
        };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("Q2_K", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_EmptyBuilds_YieldNull()
    {
        Assert.Null(QuantSelection.SelectQuant([], Budget));
        Assert.Null(QuantSelection.SelectQuant([], 0));
    }

    [Fact]
    public void SelectQuant_OnAnUnknownBudget_NeverPicksTheLargest()
    {
        // A failed probe (budget 0) must not flip everything to Fit and select
        // the biggest build (F16/F32): it goes conservative instead. The
        // no-devices/no-RAM signal (ulong.MaxValue) is the same case.
        var builds = new List<Repository>
        {
            Build("Q8_0", 12_000_000_000),
            Build("Q4_K_M", 5_000_000_000),
            Build("Q4_0", 3_000_000_000),
        };

        Assert.Equal("Q4_K_M", QuantSelection.SelectQuant(builds, 0)?.Quant);
        Assert.Equal("Q4_K_M", QuantSelection.SelectQuant(builds, ulong.MaxValue)?.Quant);
    }

    // ---- explicit GGUF file links ----

    [Fact]
    public void SelectQuant_ExplicitFileQuant_WinsOverDefaultAndFitHeuristics()
    {
        var builds = new List<Repository>
        {
            Build("Q4_K_M", 4_000_000_000),
            Build("Q8_0", 8_000_000_000),
            Build("BF16", 16_000_000_000),
        };

        // Even a tiny known budget cannot replace or reject the explicit pick.
        var pick = QuantSelection.SelectQuant(builds, 1_000_000_000, "q8_0");

        Assert.Equal("Q8_0", pick?.Quant);
        Assert.Equal("org/repo:Q8_0", pick?.ServerModelId);
    }

    [Fact]
    public void SelectQuant_ExplicitBF16_IsHonoredAndPassedToTheDownloadRequest()
    {
        var builds = new List<Repository>
        {
            Build("Q4_K_M", 4_000_000_000),
            Build("BF16", 16_000_000_000),
        };

        var pick = QuantSelection.SelectQuant(builds, 1_000_000_000, "BF16");

        Assert.Equal("BF16", pick?.Quant);
        Assert.Equal("org/repo:BF16", pick?.ServerModelId);
        // LlamaManager.DownloadModelAsync POSTs BuildModelRequestBody for
        // model.ServerModelId. Pin that final handoff, not just the selector.
        Assert.Equal("{\"model\":\"org/repo:BF16\"}",
            LlamaManager.BuildModelRequestBody(pick!.ServerModelId));
    }

    [Theory]
    [InlineData("Q2_K")]       // valid label, absent from this repo
    [InlineData("NOT_A_QUANT")] // malformed label
    [InlineData("Q4_K_M.gguf")] // not a standalone label
    public void SelectQuant_MissingOrInvalidExplicitQuant_NeverFallsBack(string requested)
    {
        var builds = new List<Repository>
        {
            Build("Q4_K_M", 4_000_000_000),
            Build("BF16", 16_000_000_000),
        };

        Assert.Null(QuantSelection.SelectQuant(builds, Budget, requested));
    }

    [Fact]
    public void SelectQuant_ExactHfLabels_PreservePrefixAndSizeVariant()
    {
        var builds = new List<Repository>
        {
            Build("Q4_K_M", 4_000_000_000),
            Build("UD-Q4_K_M", 5_000_000_000),
            Build("Q4_K_M_XXL", 6_000_000_000),
        };

        Assert.Equal("Q4_K_M", QuantSelection.SelectQuant(builds, Budget, "Q4_K_M")?.Quant);
        Assert.Equal("UD-Q4_K_M", QuantSelection.SelectQuant(builds, Budget, "ud-q4_k_m")?.Quant);
        Assert.Equal("Q4_K_M_XXL", QuantSelection.SelectQuant(builds, Budget, "Q4_K_M_XXL")?.Quant);
        // A missing prefixed request must not silently become the plain file.
        Assert.Null(QuantSelection.SelectQuant(
            new[] { Build("Q8_0", 8_000_000_000) }, Budget, "UD-Q8_0"));
    }

    [Fact]
    public void SelectQuant_PlainExplicitTag_CanResolveAPrefixedFileOnlyWhenNoExactFileExists()
    {
        var pick = QuantSelection.SelectQuant(
            new[] { Build("UD-Q4_K_M", 5_000_000_000) }, Budget, "Q4_K_M");

        Assert.Equal("UD-Q4_K_M", pick?.Quant);
        Assert.Equal("org/repo:UD-Q4_K_M", pick?.ServerModelId);
    }

    [Fact]
    public void SelectQuant_ExplicitQuant_KeepsShardSizingAndSkipsSidecars()
    {
        var files = new List<HubClient.RepoFile>
        {
            new("model-Q4_K_M-00001-of-00002.gguf", 2_000_000_000),
            new("model-Q4_K_M-00002-of-00002.gguf", 3_000_000_000),
            new("model-BF16.gguf", 16_000_000_000),
            new("mmproj-model-Q4_K_M.gguf", 1_000_000_000),
            new("mtp-model-Q4_K_M.gguf", 500_000_000),
            new("imatrix-Q4_K_M.gguf", 500_000_000),
        };

        var pick = QuantSelection.SelectQuant(
            QuantSelection.BuildsFor("org/repo", files), 1_000_000_000, "Q4_K_M");

        Assert.Equal("Q4_K_M", pick?.Quant);
        Assert.Equal(5_000_000_000ul, pick?.SizeBytes); // main shards only
        Assert.Equal("org/repo:Q4_K_M", pick?.ServerModelId);
    }

    // ---- BuildsFor: files → builds ----

    [Fact]
    public void BuildsFor_GroupsShards_SumsSizes_AndSkipsSidecars()
    {
        var files = new List<HubClient.RepoFile>
        {
            new("model-Q4_K_M-00001-of-00002.gguf", 5_000_000_000),
            new("model-Q4_K_M-00002-of-00002.gguf", 5_000_000_000), // shards sum into one build
            new("model-Q8_0.gguf", 9_000_000_000),
            new("mmproj-model-f16.gguf", 1_000_000_000),            // sidecars: never builds
            new("imatrix-Q4_K_M.gguf", 2_000_000_000),
            new("mtp-draft-Q4_0.gguf", 3_000_000_000),
            new("dflash-draft-Q4_0.gguf", 3_000_000_000),
            new("dspark-draft-Q4_0.gguf", 3_000_000_000),
            new("eagle3-draft-Q4_0.gguf", 3_000_000_000),
            new("README.md", 1),
        };

        var builds = QuantSelection.BuildsFor("org/repo", files);

        Assert.Equal(2, builds.Count); // sorted by tag
        Assert.Equal("Q4_K_M", builds[0].Quant);
        Assert.Equal("Q8_0", builds[1].Quant);
        Assert.Equal(10_000_000_000ul, builds[0].SizeBytes);
        Assert.Equal(9_000_000_000ul, builds[1].SizeBytes);
        Assert.All(builds, b => Assert.Equal("org/repo", b.Name));
        Assert.Equal("org/repo:Q4_K_M", builds[0].ServerModelId);
    }

    [Fact]
    public void BuildsFor_SkipsFilesWithoutAKnownQuantTag()
    {
        var files = new List<HubClient.RepoFile>
        {
            new("mystery-model.gguf", 5_000_000_000),
            new("model.gguf", 5_000_000_000),
        };

        Assert.Empty(QuantSelection.BuildsFor("org/repo", files));
    }

    [Theory]
    [InlineData("model-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("GLM-4.5-Air-UD-Q4_K_M.gguf", "Q4_K_M")]   // UD- prefix and version dots
    [InlineData("Qwen3-0.6B-Q4_K_M.gguf", "Q4_K_M")]       // a "Qwen3" token must not fool the parser
    [InlineData("gpt-oss-20b-F16.gguf", "F16")]
    [InlineData("model-mxfp4.gguf", "MXFP4")]              // canonical casing
    [InlineData("model-BF16.gguf", "BF16")]
    [InlineData("model-UD-Q4_K_M.gguf", "Q4_K_M")]         // canonical tag drops only the UD- prefix
    [InlineData("model-Q4_K_M_XXL.gguf", "Q4_K_M_XXL")]    // HF size variation is part of the label
    [InlineData("model-Q4_K_M-XXL.gguf", "Q4_K_M")]        // dash suffix is not part of HF's label
    [InlineData("sub/dir/model-Q8_0.gguf", "Q8_0")]        // repo-relative path
    [InlineData("mystery-model.gguf", null)]
    public void ExtractQuantTag_ReadsTheCanonicalTag(string fileName, string? expected)
    {
        Assert.Equal(expected, QuantSelection.ExtractQuantTag(fileName));
    }

    [Theory]
    [InlineData("model-UD-Q4_K_M.gguf", "UD-Q4_K_M")]
    [InlineData("model-Q4_K_M_XXL.gguf", "Q4_K_M_XXL")]
    [InlineData("sub/dir/model-bf16.gguf", "BF16")]
    [InlineData("model-Q4_K_M-XXL.gguf", "Q4_K_M")]
    public void ExtractQuantLabel_PreservesTheCompleteHfDeepLinkLabel(string fileName, string expected)
    {
        Assert.Equal(expected, QuantSelection.ExtractQuantLabel(fileName));
    }
}
