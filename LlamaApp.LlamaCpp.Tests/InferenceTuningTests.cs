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
}
