using LlamaApp.Common;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the live settings apply: <see cref="LlamaManager.LaunchDiffers"/>
/// decides whether a saved snapshot needs a server relaunch at all,
/// <see cref="LlamaManager.EndpointDiffers"/> whether the REST client must
/// also be rebuilt, and <see cref="LlamaManager.ApplyLaunchSettings"/>
/// normalizes hand-editable values (bad listen address, out-of-range port)
/// before any of that — so a settings.json edit can't push the client to a
/// foreign endpoint.
/// </summary>
public sealed class LlamaManagerLaunchSettingsTests
{
    // One shared manager for the ApplyLaunchSettings tests: Initialize is
    // one-shot per process and xUnit creates a fresh test-class instance per
    // test. Tests in a class run sequentially, so each resets the snapshot
    // first. The supervisor loop this starts polls an unused loopback port —
    // connection-refused is its steady state, with no subscribers.
    private static readonly LlamaManager Manager =
        LlamaManager.Initialize(45931, ListenAddresses.Localhost, null);

    private static LlamaManager.LaunchSettings Baseline() => new(
        9931, ListenAddresses.Localhost, null, @"C:\cache", "hf-token",
        300, 2, "q8_0", "f16", "--verbose");

    private static LlamaManager.LaunchSettings CurrentBaseline() => new(
        45931, ListenAddresses.Localhost, null, null, null, -1, 0, "f16", "f16", null);

    private static void Reset() => Manager.ApplyLaunchSettings(CurrentBaseline());

    // ---- LaunchDiffers / EndpointDiffers (pure) ----

    [Fact]
    public void IdenticalSnapshots_DifferInNothing()
    {
        Assert.False(LlamaManager.LaunchDiffers(Baseline(), Baseline()));
        Assert.False(LlamaManager.EndpointDiffers(Baseline(), Baseline()));
    }

    [Theory]
    [InlineData(9932, "127.0.0.1", null)]    // port
    [InlineData(9931, "0.0.0.0", null)]      // listen address
    [InlineData(9931, "127.0.0.1", "key")]   // API key
    public void EndpointChange_CountsAsBoth(int port, string listen, string? apiKey)
    {
        var next = Baseline() with { ServerPort = port, ListenAddress = listen, ApiKey = apiKey };

        Assert.True(LlamaManager.EndpointDiffers(Baseline(), next));
        Assert.True(LlamaManager.LaunchDiffers(Baseline(), next));
    }

    [Fact]
    public void LaunchFlagChange_IsNotAnEndpointChange()
    {
        var next = Baseline() with
        {
            CacheDirectory = @"D:\other",
            HuggingFaceToken = "other-token",
            IdleUnloadSeconds = 600,
            MaxLoadedModels = 5,
            CacheTypeK = "q4_0",
            CacheTypeV = "q8_0",
            CustomServeArguments = "--flash-attn",
        };

        Assert.True(LlamaManager.LaunchDiffers(Baseline(), next));
        Assert.False(LlamaManager.EndpointDiffers(Baseline(), next));
    }

    [Theory]
    [InlineData("CacheDirectory", @"D:\other")]
    [InlineData("HuggingFaceToken", "other-token")]
    [InlineData("CustomServeArguments", "--flash-attn")]
    public void EachLaunchEnvField_TriggersTheDiff(string field, string value)
    {
        var next = field switch
        {
            "CacheDirectory" => Baseline() with { CacheDirectory = value },
            "HuggingFaceToken" => Baseline() with { HuggingFaceToken = value },
            _ => Baseline() with { CustomServeArguments = value },
        };

        Assert.True(LlamaManager.LaunchDiffers(Baseline(), next));
        Assert.False(LlamaManager.EndpointDiffers(Baseline(), next));
    }

    // ---- ApplyLaunchSettings (normalization + live values) ----

    [Fact]
    public void NoChange_ReturnsFalse()
    {
        Reset();

        Assert.False(Manager.ApplyLaunchSettings(CurrentBaseline()));
    }

    [Fact]
    public void LaunchFlagChange_AppliesWithoutTouchingTheEndpoint()
    {
        Reset();

        var changed = Manager.ApplyLaunchSettings(CurrentBaseline() with { IdleUnloadSeconds = 600, CacheTypeK = "q8_0" });

        Assert.True(changed);
        Assert.Equal(600, Manager.IdleUnloadSeconds);
        Assert.Equal("q8_0", Manager.CacheTypeK);
        Assert.Equal(45931, Manager.ServerPort);
        Assert.Equal(ListenAddresses.Localhost, Manager.ListenAddress);
    }

    [Fact]
    public void PortChange_RebindsTheClient()
    {
        Reset();

        var changed = Manager.ApplyLaunchSettings(CurrentBaseline() with { ServerPort = 45932 });

        Assert.True(changed);
        Assert.Equal(45932, Manager.ServerPort);
        Assert.Equal(ListenAddresses.Localhost, Manager.ConnectAddress);
    }

    [Fact]
    public void AllInterfacesBind_KeepsTheClientOnLoopback()
    {
        Reset();

        var changed = Manager.ApplyLaunchSettings(
            CurrentBaseline() with { ListenAddress = ListenAddresses.AllInterfaces, ApiKey = "abc123" });

        Assert.True(changed);
        Assert.Equal(ListenAddresses.AllInterfaces, Manager.ListenAddress);
        Assert.Equal(ListenAddresses.Localhost, Manager.ConnectAddress);
        Assert.Equal("abc123", Manager.ApiKey);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    public void BadListenAddress_FallsBackToLoopback(string bad)
    {
        Reset();
        Manager.ApplyLaunchSettings(CurrentBaseline() with { ListenAddress = ListenAddresses.AllInterfaces, ApiKey = "k" });

        var changed = Manager.ApplyLaunchSettings(CurrentBaseline() with { ListenAddress = bad });

        Assert.True(changed); // 0.0.0.0 → loopback is a real endpoint change
        Assert.Equal(ListenAddresses.Localhost, Manager.ListenAddress);
        Assert.Null(Manager.ApiKey); // loopback needs no key; "k" is gone
    }

    [Fact]
    public void OutOfRangePort_KeepsTheCurrentOne()
    {
        Reset();

        Assert.False(Manager.ApplyLaunchSettings(CurrentBaseline() with { ServerPort = 0 }));
        Assert.Equal(45931, Manager.ServerPort);
    }

    [Fact]
    public void BlankApiKey_ReadsAsNone()
    {
        Reset();

        Assert.False(Manager.ApplyLaunchSettings(CurrentBaseline() with { ApiKey = "   " }));
        Assert.Null(Manager.ApiKey);
    }

    [Fact]
    public async Task RestartServer_WithoutARunningServer_IsANoOp()
    {
        Reset();

        Assert.False(await Manager.RestartServerAsync());
    }
}
