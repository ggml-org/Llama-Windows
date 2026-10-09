using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the argv matrix assembled when the app launches a llama server:
/// <c>BuildServeArguments</c> owns the fixed flags plus the conditional
/// <c>--sleep-idle-seconds</c>, <c>--models-max</c>, <c>--cache-type-k</c>,
/// <c>--cache-type-v</c>, and <c>--models-preset</c> flags, so changes don't
/// accidentally reorder or drop flags.
/// </summary>
public sealed class LlamaManagerArgumentsTests
{
    [Fact]
    public void BuildServeArguments_AlwaysStartsWithServePortAndJinja()
    {
        var args = LlamaManager.BuildServeArguments(9931, -1, 0, null, null, null);

        Assert.Equal(
            new[] { "serve", "--port", "9931", "--jinja" },
            args);
    }

    [Fact]
    public void BuildServeArguments_OmitsOptionalFlags_AtDefaults()
    {
        // -1 = idle unload disabled; 0 = unlimited loaded models; K/V types
        // blank = keep the server's default (no flag).
        var args = LlamaManager.BuildServeArguments(9931, -1, 0, null, null, null);

        Assert.DoesNotContain("--sleep-idle-seconds", args);
        Assert.DoesNotContain("--models-max", args);
        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
        Assert.DoesNotContain("--models-preset", args);
    }

    [Fact]
    public void BuildServeArguments_AddsModelsMax_WhenPositive()
    {
        var args = LlamaManager.BuildServeArguments(9931, -1, 3, null, null, null);

        Assert.Contains("--models-max", args);
        var flagIndex = args.IndexOf("--models-max");
        Assert.True(flagIndex >= 0);
        Assert.Equal("3", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_AddsSleepIdleSeconds_WhenPositive()
    {
        var args = LlamaManager.BuildServeArguments(9931, 900, 0, null, null, null);

        Assert.Contains("--sleep-idle-seconds", args);
        var flagIndex = args.IndexOf("--sleep-idle-seconds");
        Assert.Equal("900", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_AddsModelsPreset_WhenPathProvided()
    {
        var args = LlamaManager.BuildServeArguments(9931, -1, 0, null, null, @"C:\temp\models.ini");

        Assert.Contains("--models-preset", args);
        var flagIndex = args.IndexOf("--models-preset");
        Assert.Equal(@"C:\temp\models.ini", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_MaxModelsZeroOrNegative_MeansUnlimited()
    {
        foreach (var value in new[] { 0, -1 })
        {
            var args = LlamaManager.BuildServeArguments(9931, -1, value, null, null, null);
            Assert.DoesNotContain("--models-max", args);
        }
    }

    [Fact]
    public void BuildServeArguments_AddsCacheTypeKAndV_WhenConfigured()
    {
        var args = LlamaManager.BuildServeArguments(9931, -1, 0, "q8_0", "q4_0", null);

        var kIndex = args.IndexOf("--cache-type-k");
        var vIndex = args.IndexOf("--cache-type-v");
        Assert.True(kIndex >= 0);
        Assert.True(vIndex >= 0);
        Assert.Equal("q8_0", args[kIndex + 1]);
        Assert.Equal("q4_0", args[vIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_OmitsEmptyCacheTypes()
    {
        var args = LlamaManager.BuildServeArguments(9931, -1, 0, "", " ", null);

        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
    }

    [Fact]
    public void BuildServeArguments_OmitsUnsupportedCacheTypes()
    {
        var args = LlamaManager.BuildServeArguments(9931, -1, 0, "foo", "q8", null);

        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
    }
}
