using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the pure <c>llama://install</c> quant choice
/// (<see cref="QuantSelection.SelectQuant"/>) and the build derivation it
/// works on (<see cref="QuantSelection.BuildsFor"/>): the rule is
/// "prefer the largest quant whose fit is Fit/Might for this machine's
/// budget", refuse when nothing fits on a KNOWN budget, and fall back to a
/// conservative known quant when the budget is unknown or the sizes can't be
/// ranked — so the tests pin largest-fitting, refuse, and the conservative /
/// deterministic fallbacks, plus the sidecar/shard handling that feeds it.
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

    // ---- largest-fitting ----

    [Fact]
    public void SelectQuant_PrefersTheLargestQuantWhoseFitIsFitOrMight()
    {
        var builds = new List<Repository>
        {
            Build("Q2_K", 2_000_000_000),   // Fit
            Build("Q4_K_M", 5_000_000_000), // Fit
            Build("Q8_0", 12_000_000_000),  // Might — still acceptable, and larger
            Build("F16", 20_000_000_000),   // NoWay — never picked
        };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("Q8_0", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_PicksTheLargest_WhenAllFit()
    {
        var builds = new List<Repository>
        {
            Build("Q4_0", 3_000_000_000), // Fit
            Build("Q8_0", 8_000_000_000), // Fit, largest
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
            Build("Q4_0", 4_000_000_000),
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
    [InlineData("model-Q4_K_M-XXL.gguf", "Q4_K_M")]        // size-variant suffixes
    [InlineData("sub/dir/model-Q8_0.gguf", "Q8_0")]        // repo-relative path
    [InlineData("mystery-model.gguf", null)]
    public void ExtractQuantTag_ReadsTheCanonicalTag(string fileName, string? expected)
    {
        Assert.Equal(expected, QuantSelection.ExtractQuantTag(fileName));
    }
}
