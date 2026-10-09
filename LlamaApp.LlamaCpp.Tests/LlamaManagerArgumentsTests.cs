using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the argv matrix assembled when the app launches a llama server:
/// <c>BuildServeArguments</c> owns the fixed prefix (<c>serve --host …
/// --port … --jinja</c>) plus the conditional <c>--sleep-idle-seconds</c>,
/// <c>--models-max</c>, <c>--cache-type-k</c>, <c>--cache-type-v</c>, and
/// <c>--models-preset</c> flags, plus the trailing custom-argument tokens, so
/// changes don't accidentally reorder or drop flags.
/// </summary>
public sealed class LlamaManagerArgumentsTests
{
    [Fact]
    public void BuildServeArguments_AlwaysStartsWithServeHostPortAndJinja()
    {
        var args = LlamaManager.BuildServeArguments(9931, "0.0.0.0", -1, 0, null, null, null, null);

        Assert.Equal(
            new[] { "serve", "--host", "0.0.0.0", "--port", "9931", "--jinja" },
            args);
    }

    [Fact]
    public void BuildServeArguments_OmitsOptionalFlags_AtDefaults()
    {
        // -1 = idle unload disabled; 0 = unlimited loaded models; K/V types
        // blank = keep the server's default (no flag).
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, null, null, null, null);

        Assert.DoesNotContain("--sleep-idle-seconds", args);
        Assert.DoesNotContain("--models-max", args);
        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
        Assert.DoesNotContain("--models-preset", args);
    }

    [Fact]
    public void BuildServeArguments_AddsModelsMax_WhenPositive()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 3, null, null, null, null);

        Assert.Contains("--models-max", args);
        var flagIndex = args.IndexOf("--models-max");
        Assert.True(flagIndex >= 0);
        Assert.Equal("3", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_AddsSleepIdleSeconds_WhenPositive()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", 900, 0, null, null, null, null);

        Assert.Contains("--sleep-idle-seconds", args);
        var flagIndex = args.IndexOf("--sleep-idle-seconds");
        Assert.Equal("900", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_AddsModelsPreset_WhenPathProvided()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, null, null, @"C:\temp\models.ini", null);

        Assert.Contains("--models-preset", args);
        var flagIndex = args.IndexOf("--models-preset");
        Assert.Equal(@"C:\temp\models.ini", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_MaxModelsZeroOrNegative_MeansUnlimited()
    {
        foreach (var value in new[] { 0, -1 })
        {
            var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, value, null, null, null, null);
            Assert.DoesNotContain("--models-max", args);
        }
    }

    [Fact]
    public void BuildServeArguments_AddsCacheTypeKAndV_WhenConfigured()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, "q8_0", "q4_0", null, null);

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
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, "", " ", null, null);

        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
    }

    [Fact]
    public void BuildServeArguments_OmitsUnsupportedCacheTypes()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, "foo", "q8", null, null);

        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
    }

    [Fact]
    public void BuildServeArguments_AppendsCustomArgumentsLast()
    {
        var custom = new[] { "--threads", "4", "--flash-attn" };

        var args = LlamaManager.BuildServeArguments(
            9931, "127.0.0.1", -1, 0, null, null, @"C:\temp\models.ini", custom);

        // Custom tokens come after the built-ins so they win when llama.cpp
        // honors the last occurrence of a repeated flag.
        Assert.Equal(custom, args[^custom.Length..]);
        Assert.Equal(@"C:\temp\models.ini", args[args.Count - custom.Length - 1]);
    }

    [Fact]
    public void BuildServeArguments_EmptyCustomArguments_ChangeNothing()
    {
        var without = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, null, null, null, null);
        var withEmpty = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, null, null, null, Array.Empty<string>());

        Assert.Equal(without, withEmpty);
    }

    // ----- Listen-address forwarding -----------------------------------------

    [Fact]
    public void BuildServeArguments_ForwardsTheSelectedListenAddress()
    {
        var args = LlamaManager.BuildServeArguments(9931, "192.168.1.10", -1, 0, null, null, null, null);

        var hostIndex = args.IndexOf("--host");
        Assert.True(hostIndex >= 0);
        Assert.Equal("192.168.1.10", args[hostIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_KeepsAllInterfacesAndLocalhostVerbatim()
    {
        foreach (var address in new[] { "0.0.0.0", "127.0.0.1" })
        {
            var args = LlamaManager.BuildServeArguments(9931, address, -1, 0, null, null, null, null);
            var hostIndex = args.IndexOf("--host");
            Assert.Equal(address, args[hostIndex + 1]);
        }
    }

    [Fact]
    public void ConnectAddressFor_MapsAllInterfacesToLocalhost()
    {
        Assert.Equal(
            LlamaApp.Common.ListenAddresses.Localhost,
            LlamaManager.ConnectAddressFor(LlamaApp.Common.ListenAddresses.AllInterfaces));
    }

    [Fact]
    public void ConnectAddressFor_KeepsSpecificAddressesVerbatim()
    {
        Assert.Equal(
            "192.168.1.42",
            LlamaManager.ConnectAddressFor("192.168.1.42"));
        Assert.Equal(
            LlamaApp.Common.ListenAddresses.Localhost,
            LlamaManager.ConnectAddressFor(LlamaApp.Common.ListenAddresses.Localhost));
    }

    // ----- Launch-failure phrasing -------------------------------------------

    [Fact]
    public void FormatStartFailureMessage_WithoutStderr_IsGeneric()
    {
        Assert.Equal(
            "The llama server failed to start.",
            LlamaManager.FormatStartFailureMessage(null));
        Assert.Equal(
            "The llama server failed to start.",
            LlamaManager.FormatStartFailureMessage("  "));
    }

    [Fact]
    public void FormatStartFailureMessage_WithStderr_IncludesIt()
    {
        Assert.Equal(
            "The llama server failed to start: error: unknown flag --nope",
            LlamaManager.FormatStartFailureMessage(" error: unknown flag --nope "));
    }

    [Fact]
    public void FormatStartFailureMessage_PrefersTheErrorLine_OverInfoChatter()
    {
        // llama.cpp writes INFO lines to stderr too; the reason shown on the
        // status dot must be the actual error, not the last INFO line.
        const string tail = """
            INFO: loading model
            INFO: server is listening
            error: invalid argument: --nope
            usage: llama serve [options]
            """;

        Assert.Equal(
            "The llama server failed to start: error: invalid argument: --nope",
            LlamaManager.FormatStartFailureMessage(tail));
    }

    [Fact]
    public void FormatStartFailureMessage_WithoutAnErrorLine_UsesTheLastLine()
    {
        const string tail = """
            INFO: loading model
            something else went wrong
            """;

        Assert.Equal(
            "The llama server failed to start: something else went wrong",
            LlamaManager.FormatStartFailureMessage(tail));
    }
}
