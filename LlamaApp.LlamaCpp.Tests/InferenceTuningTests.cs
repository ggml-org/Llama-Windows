using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

public sealed class InferenceTuningTests
{
    [Fact]
    public void AutomaticSettingsLeaveServeArgumentsUnchanged()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0,
            null, null, null, null, batchSize: 0, microBatchSize: 0,
            flashAttention: "auto");
        Assert.DoesNotContain("--batch-size", args);
        Assert.DoesNotContain("--ubatch-size", args);
        Assert.DoesNotContain("--flash-attn", args);
    }

    [Fact]
    public void ValidOverridesReachTheServer()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0,
            null, null, null, null, batchSize: 1024, microBatchSize: 256,
            flashAttention: "on");
        Assert.Equal("1024", args[args.IndexOf("--batch-size") + 1]);
        Assert.Equal("256", args[args.IndexOf("--ubatch-size") + 1]);
        Assert.Equal("on", args[args.IndexOf("--flash-attn") + 1]);
    }

    [Theory]
    [InlineData(512, 1024, "auto")]
    [InlineData(-1, 0, "auto")]
    [InlineData(0, 0, "sometimes")]
    public void InvalidOverridesAreRejected(int batch, int microbatch, string flash)
        => Assert.NotNull(InferenceTuning.Validate(batch, microbatch, flash));

    [Fact]
    public void CapabilityProbeDistinguishesSupportedAndMissingFlags()
    {
        var capabilities = ServeCapabilities.Parse(
            "--help print usage\n--port PORT\n--batch-size N\n--ubatch-size N\n");
        Assert.True(capabilities.Succeeded);
        Assert.Null(InferenceTuning.UnsupportedOption(512, 256, "auto", capabilities));
        Assert.Equal("--flash-attn",
            InferenceTuning.UnsupportedOption(512, 256, "on", capabilities));
    }

    [Fact]
    public void UnreadableHelpCannotAuthorizeAnOverride()
    {
        var capabilities = ServeCapabilities.Parse("unknown command");
        Assert.False(capabilities.Succeeded);
        Assert.NotNull(InferenceTuning.UnsupportedOption(512, 0, "auto", capabilities));
    }

    [Fact]
    public void ModelProfileValidatesInheritedBatchAndMicrobatchSizes()
    {
        Assert.NotNull(InferenceTuning.ValidateEffective(128, 0, 0, 256, "auto"));
        Assert.NotNull(InferenceTuning.ValidateEffective(2048, 512, 128, 0, "auto"));
        Assert.Null(InferenceTuning.ValidateEffective(2048, 512, 1024, 0, "on"));
    }

    [Fact]
    public void ExpertProfileRejectsInvalidRangesAndTensorSplit()
    {
        Assert.NotNull(InferenceTuning.ValidateProfile(new() { Temperature = double.NaN }));
        Assert.NotNull(InferenceTuning.ValidateProfile(new() { TopP = 1.1 }));
        Assert.NotNull(InferenceTuning.ValidateProfile(new() { TensorSplit = "1,-2" }));
        Assert.Null(InferenceTuning.ValidateProfile(new()
            { Threads = 8, Parallel = 2, SplitMode = "layer", TensorSplit = "3,1" }));
    }

    [Fact]
    public void CapabilityProbeRequiresTheExactExpertFlag()
    {
        var capabilities = ServeCapabilities.Parse("--help\n--port N\n--threads-batch N\n");
        Assert.False(capabilities.Supports("--threads"));
        Assert.True(capabilities.Supports("--threads-batch"));
        Assert.Equal("--threads", InferenceTuning.UnsupportedProfileOption(
            new() { Threads = 4 }, capabilities));
    }

    [Fact]
    public void UnsupportedModelProfileIsOmittedWithoutLosingModelContextOrPath()
    {
        var profiles = new Dictionary<string, ModelPromptProcessingProfile>
        {
            ["local/unsupported"] = new() { FlashAttention = "on", Threads = 8 },
            ["supported"] = new() { Threads = 4 },
        };
        var capabilities = ServeCapabilities.Parse("--help\n--port N\n--threads N\n");

        var compatible = InferenceTuning.CompatibleModelProfiles(profiles, 0, 0,
            capabilities, out var warnings);
        Assert.Single(compatible);
        Assert.Equal(profiles["supported"], compatible["supported"]);
        Assert.Contains("--flash-attn", Assert.Single(warnings));
        Assert.Contains("local/unsupported", warnings[0]);

        var ini = ModelPresets.Render(
            new Dictionary<string, int> { ["local/unsupported"] = 4096 }, compatible,
            localModelPaths: new Dictionary<string, string>
                { ["local/unsupported"] = @"C:\Models\weights.gguf" });
        Assert.Contains("[local/unsupported]", ini);
        Assert.Contains("ctx-size = 4096", ini);
        Assert.Contains(@"model = C:\Models\weights.gguf", ini);
        Assert.DoesNotContain("flash-attn", ini);
        Assert.DoesNotContain("threads = 8", ini);
        Assert.Contains("threads = 4", ini);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("on", profiles["local/unsupported"].FlashAttention);
    }

    [Fact]
    public void FailedCapabilityProbeDoesNotBlockAutomaticModelProfiles()
    {
        var profiles = new Dictionary<string, ModelPromptProcessingProfile>
        {
            ["automatic"] = new(),
            ["advanced"] = new() { Threads = 4 },
        };
        var compatible = InferenceTuning.CompatibleModelProfiles(profiles, 0, 0,
            ServeCapabilities.Parse(null), out var warnings);
        Assert.Single(compatible);
        Assert.Contains("automatic", compatible.Keys);
        Assert.Contains("Could not check", Assert.Single(warnings));
    }

    [Fact]
    public void InvalidAndInheritedIncompatibleProfilesAreOmittedIndividually()
    {
        var profiles = new Dictionary<string, ModelPromptProcessingProfile>
        {
            ["invalid"] = new() { Temperature = double.NaN },
            ["inherited"] = new() { MicroBatchSize = 256 },
            ["valid"] = new() { Threads = 4 },
        };
        var capabilities = ServeCapabilities.Parse("--help\n--port N\n--threads N\n--ubatch-size N\n");
        var compatible = InferenceTuning.CompatibleModelProfiles(profiles, 128, 0,
            capabilities, out var warnings);
        Assert.Equal(2, warnings.Count);
        Assert.Equal("valid", Assert.Single(compatible).Key);
    }

    [Fact]
    public void ProfileCanBeRestoredAfterRuntimeUpgradeOrClearedWithReset()
    {
        var profiles = new Dictionary<string, ModelPromptProcessingProfile>
        {
            ["model"] = new() { FlashAttention = "on" },
        };
        Assert.Empty(InferenceTuning.CompatibleModelProfiles(profiles, 0, 0,
            ServeCapabilities.Parse("--help\n--port N\n"), out _));
        var compatible = InferenceTuning.CompatibleModelProfiles(profiles, 0, 0,
            ServeCapabilities.Parse("--help\n--port N\n--flash-attn MODE\n"), out var warnings);
        Assert.Single(compatible);
        Assert.Empty(warnings);

        profiles.Clear();
        Assert.Empty(InferenceTuning.CompatibleModelProfiles(profiles, 0, 0,
            ServeCapabilities.Parse(null), out warnings));
        Assert.Empty(warnings);
    }
}
