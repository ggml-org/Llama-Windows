using LlamaApp.HuggingFace;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the pure <c>llama://install</c> quant choice
/// (<see cref="QuantSelection.SelectQuant"/>) and the build derivation it
/// works on (<see cref="QuantSelection.BuildsFor"/>): the rule is
/// "prefer the largest quant whose fit is Fit/Might for this machine's
/// budget", with a documented conservative fallback when nothing fits or the
/// sizes can't be ranked — so the tests pin exactly those cases
/// (largest-fitting, none-fits fallback, unknown-sizes default) plus the
/// sidecar/shard handling that feeds it.
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
    public void SelectQuant_SingleNoWayBuild_IsTheFallbackPick()
    {
        // One build, comfortably past the optimistic band: the largest-
        // fitting rule yields nothing, so the none-fits fallback applies —
        // and the only build the repo offers (the smallest) is the pick.
        var builds = new List<Repository> { Build("F16", 20_000_000_000) };

        var pick = QuantSelection.SelectQuant(builds, Budget);

        Assert.Equal("F16", pick?.Quant);
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
    public void SelectQuant_WhenNoneFits_FallsBackToTheConservativeKnownQuant()
    {
        // Budget of 1 GB: every build is NoWay. The fallback is the
        // conservative known quant (Q4_K_M — llama.cpp's default tag), NOT
        // the smallest build.
        var builds = new List<Repository>
        {
            Build("Q2_K", 2_000_000_000),
            Build("Q4_K_M", 4_000_000_000),
            Build("Q6_K", 6_000_000_000),
        };

        var pick = QuantSelection.SelectQuant(builds, 1_000_000_000);

        Assert.Equal("Q4_K_M", pick?.Quant);
    }

    [Fact]
    public void SelectQuant_WhenNoneFits_AndNoConservativeQuant_TakesTheSmallest()
    {
        // No Q4_K_M/Q4_0/Q8_0 in the repo: the most conservative build the
        // repo actually offers is the smallest one.
        var builds = new List<Repository>
        {
            Build("Q6_K", 6_000_000_000),
            Build("Q5_K_S", 5_000_000_000),
        };

        var pick = QuantSelection.SelectQuant(builds, 1_000_000_000);

        Assert.Equal("Q5_K_S", pick?.Quant);
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
    public void SelectQuant_WhenSizesAreUnknown_AndNoConservativeQuant_YieldsNull()
    {
        // Nothing to rank and no conservative tag: null tells the caller to
        // request the conservative quant (repo:Q4_K_M) directly.
        var builds = new List<Repository>
        {
            Build("F16", 0),
            Build("Q8_0", 0),
        };

        Assert.Null(QuantSelection.SelectQuant(builds, Budget));
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
