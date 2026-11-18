using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the argv matrix assembled when the app launches a llama server:
/// <c>BuildServeArguments</c> owns the fixed prefix (<c>serve --host …
/// --port … --jinja</c>) plus the always-emitted <c>--models-max</c> /
/// <c>--models-memory-margin</c> pair and the conditional
/// <c>--sleep-idle-seconds</c>, <c>--cache-type-k</c>, <c>--cache-type-v</c>,
/// and <c>--models-preset</c> flags, plus the trailing custom-argument tokens,
/// so changes don't accidentally reorder or drop flags. Also covers the pure
/// external-engine floor (<c>ExternalEngineSupportsLaunchFlags</c>) and the
/// managed-vs-external binary selection (<c>PreferManagedBinary</c>) that gate
/// the launch.
/// </summary>
public sealed class LlamaManagerArgumentsTests
{
    [Fact]
    public void BuildServeArguments_AlwaysStartsWithServeHostPortAndJinja()
    {
        var args = LlamaManager.BuildServeArguments(9931, "0.0.0.0", -1, 0, null, null, null, null);

        Assert.Equal(
            new[] { "serve", "--host", "0.0.0.0", "--port", "9931", "--jinja" },
            args.Take(6));
    }

    [Fact]
    public void BuildServeArguments_OmitsRemainingOptionalFlags_AtDefaults()
    {
        // -1 = idle unload disabled; 0 = unlimited loaded models (emitted as
        // an explicit --models-max 0); K/V types blank = keep the server's
        // default (no flag).
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, null, null, null, null);

        Assert.DoesNotContain("--sleep-idle-seconds", args);
        Assert.DoesNotContain("--cache-type-k", args);
        Assert.DoesNotContain("--cache-type-v", args);
        Assert.DoesNotContain("--models-preset", args);
    }

    [Fact]
    public void BuildServeArguments_EmitsModelsMaxZeroAndMargin_AtDefaults()
    {
        // The default 0 is not "omitted": it is passed explicitly so the
        // server's own unlimited count applies with a known argv — together
        // with the fixed memory margin that decides when a load waits.
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 0, null, null, null, null);

        var maxIndex = args.IndexOf("--models-max");
        Assert.True(maxIndex >= 0);
        Assert.Equal("0", args[maxIndex + 1]);
        var marginIndex = args.IndexOf("--models-memory-margin");
        Assert.True(marginIndex >= 0);
        Assert.Equal("1024", args[marginIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_SafeMemoryDefaults_AreEmitted()
    {
        // New/migrated Settings use 300 seconds and zero cap (unlimited count
        // under the memory margin). Keep the launch mapping covered so those
        // defaults cannot silently turn back into omitted (unknown) server
        // flags.
        var args = LlamaManager.BuildServeArguments(
            9931, "127.0.0.1", 300, 1, null, null, null, null);

        var idleIndex = args.IndexOf("--sleep-idle-seconds");
        Assert.True(idleIndex >= 0);
        Assert.Equal("300", args[idleIndex + 1]);
        var maxIndex = args.IndexOf("--models-max");
        Assert.True(maxIndex >= 0);
        Assert.Equal("1", args[maxIndex + 1]);
        var marginIndex = args.IndexOf("--models-memory-margin");
        Assert.True(marginIndex >= 0);
        Assert.Equal("1024", args[marginIndex + 1]);
    }

    [Fact]
    public void BuildServeArguments_AddsModelsMax_WhenPositive()
    {
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, 3, null, null, null, null);

        Assert.Contains("--models-max", args);
        var flagIndex = args.IndexOf("--models-max");
        Assert.True(flagIndex >= 0);
        Assert.Equal("3", args[flagIndex + 1]);
        var marginIndex = args.IndexOf("--models-memory-margin");
        Assert.True(marginIndex >= 0);
        Assert.Equal("1024", args[marginIndex + 1]);
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void BuildServeArguments_MaxModelsZeroOrNegative_ClampsToZero(int value)
    {
        // 0 and negative = no count cap, emitted as an explicit --models-max 0
        // so the server never falls back to its own per-build default.
        var args = LlamaManager.BuildServeArguments(9931, "127.0.0.1", -1, value, null, null, null, null);

        var flagIndex = args.IndexOf("--models-max");
        Assert.True(flagIndex >= 0);
        Assert.Equal("0", args[flagIndex + 1]);
    }

    // ---- API key: env-only delivery (never argv) ----

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    public void BuildServeArguments_NeverEmitsApiKeyFlags(string listenAddress)
    {
        // The key is delivered via the LLAMA_API_KEY environment variable (see
        // LlamaManager.StartServerAsync), never as a process argument — argv is
        // readable by any local user via WMI / process listings. So no API-key
        // flag of any kind may appear in the built arguments, for any bind.
        var args = LlamaManager.BuildServeArguments(
            9931, listenAddress, -1, 0, null, null, null, null);

        Assert.DoesNotContain("--api-key", args);
        Assert.DoesNotContain("--api-key-file", args);
    }

    // ---- --models-dir ("Open With" local models) ----

    [Fact]
    public void BuildServeArguments_AddsModelsDir_WhenProvided()
    {
        var args = LlamaManager.BuildServeArguments(
            9931, "127.0.0.1", -1, 0, null, null, null, null,
            modelsDir: @"C:\Users\me\AppData\Local\Llama\models");

        var flagIndex = args.IndexOf("--models-dir");
        Assert.True(flagIndex >= 0);
        Assert.Equal(@"C:\Users\me\AppData\Local\Llama\models", args[flagIndex + 1]);
        // App-owned block: sits with the other built-ins, before the custom tail.
        Assert.True(flagIndex < args.IndexOf("--jinja") + 2);
    }

    [Fact]
    public void BuildServeArguments_OmitsModelsDir_WhenNull()
    {
        var args = LlamaManager.BuildServeArguments(
            9931, "127.0.0.1", -1, 0, null, null, null, null, modelsDir: null);

        Assert.DoesNotContain("--models-dir", args);
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

    [Fact]
    public void FormatStartFailureMessage_WhenPresetRecoveryIsDeclined_IsTheOrdinaryFailure()
    {
        // A preset rejection whose one-shot recovery is declined (no active
        // user overrides, or the retry is already spent) degrades to an
        // ORDINARY launch failure: the standard phrasing with the server's
        // diagnostic as detail — no special "rejected" story, and nothing
        // swallowed.
        const string tail =
            "0.02.238.560 E srv start: option 'temperture' not recognized in preset 'model-context-presets.ini'";

        Assert.Equal(
            $"The llama server failed to start: {tail}",
            LlamaManager.FormatStartFailureMessage(tail));
    }

    [Fact]
    public void FormatStartFailureMessage_UsesTheBoundedSnapshot_NotTheRejectionLatch()
    {
        // The recovery DECISION reads the tail's revisioned latch; the failure
        // PHRASING is built from the bounded 20-line snapshot. When the
        // rejection line has been evicted, the message carries only what the
        // snapshot still holds — it never resurrects a latched diagnostic the
        // user can no longer see in the captured output.
        var tail = new LlamaManager.ServerOutputTail();
        tail.Add("0.02.238.560 E srv start: option 'temperture' not recognized in preset 'model-context-presets.ini'");
        for (var i = 0; i < 25; i++) tail.Add($"chatter line {i}");

        var message = LlamaManager.FormatStartFailureMessage(
            tail.Snapshot(), 9931, LlamaApp.Common.ListenAddresses.Localhost, timedOut: true);

        Assert.DoesNotContain("temperture", message);
        Assert.Contains("chatter line 24", message);
    }

    // ----- External engine floor (b11930) ----------------------------------

    [Theory]
    [InlineData("llama-server (llama) b11930 (abcdef)")]
    [InlineData("version: 0.7.0-dev (build 11941, commit 19ff817fb)")]
    [InlineData("llama-server (llama) b12000")]
    [InlineData("version: 11930 (abcdef)")]
    public void ExternalEngineSupportsLaunchFlags_AcceptsB11930AndNewer(string versionLine)
    {
        // --models-memory-margin exists from b11930 on, so these engines can
        // be launched as-is — no managed-install fallback.
        Assert.True(LlamaManager.ExternalEngineSupportsLaunchFlags(versionLine));
    }

    [Theory]
    [InlineData("llama-server (llama) b11929 (abcdef)")]
    [InlineData("version: 6726 (abcdef)")]
    [InlineData("llama-server (llama) b9553 (build: 9553)")]
    [InlineData("version: 0.8.0")]
    public void ExternalEngineSupportsLaunchFlags_RejectsOlderBuilds(string versionLine)
    {
        // A bare "version: 0.8.0" parses as build 0 (below the floor) — an
        // ambiguous engine is treated like an old one, never launched blindly.
        Assert.False(LlamaManager.ExternalEngineSupportsLaunchFlags(versionLine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some unrecognizable banner")]
    public void ExternalEngineSupportsLaunchFlags_RejectsUnreadableVersions(string? versionLine)
    {
        // An unreadable version cannot prove the flag exists — rejected so the
        // ensure pipeline installs the managed build instead of launching a
        // possibly-unsupported engine.
        Assert.False(LlamaManager.ExternalEngineSupportsLaunchFlags(versionLine));
    }

    [Fact]
    public void ExternalEngineSupportsLaunchFlags_UsesTheFloorBoundary()
    {
        Assert.Equal(11930u, LlamaManager.MinimumExternalEngineBuild);
    }

    // ----- Managed vs external binary preference ---------------------------

    // The version strings the selection rule classifies (see
    // ExternalEngineSupportsLaunchFlags above): "b12000" is a supported
    // external engine, "b9553" an unsupported one, null an unreadable one.

    [Fact]
    public void PreferManagedBinary_SupportedExternalWinsOverManaged()
    {
        // The regression this guards: a PATH hit at or above the b11930 floor
        // is the user's own supported engine — it must win over the managed
        // install regardless of PATH order or install recency.
        Assert.Equal(
            @"C:\external\llama.exe",
            LlamaManager.PreferManagedBinary(
                @"C:\external\llama.exe", @"C:\managed\llama.exe", "llama-server (llama) b12000 (abcdef)"));
    }

    [Fact]
    public void PreferManagedBinary_UnsupportedExternalFallsBackToManaged()
    {
        // Below the floor the external engine would die at launch on
        // --models-memory-margin — the managed install wins instead.
        Assert.Equal(
            @"C:\managed\llama.exe",
            LlamaManager.PreferManagedBinary(
                @"C:\external\llama.exe", @"C:\managed\llama.exe", "llama-server (llama) b9553 (abcdef)"));
    }

    [Fact]
    public void PreferManagedBinary_UnreadableExternalFallsBackToManaged()
    {
        // An unreadable version cannot prove the flag exists — same fallback
        // as an old engine, never launched blindly over the managed build.
        Assert.Equal(
            @"C:\managed\llama.exe",
            LlamaManager.PreferManagedBinary(
                @"C:\external\llama.exe", @"C:\managed\llama.exe", null));
    }

    [Theory]
    [InlineData("llama-server (llama) b9553 (abcdef)")]
    [InlineData("some unrecognizable banner")]
    [InlineData(null)]
    public void PreferManagedBinary_OldExternalAfterInstall_IsNotReselected(string? versionLine)
    {
        // The post-install case: an older/unreadable external hit earlier on
        // PATH must not re-shadow the managed build that was just installed.
        Assert.Equal(
            @"C:\managed\llama.exe",
            LlamaManager.PreferManagedBinary(
                @"C:\external\llama.exe", @"C:\managed\llama.exe", versionLine));
    }

    [Fact]
    public void PreferManagedBinary_NoManagedBinary_UsesExternal()
    {
        // Managed missing, external present: the external engine is the only
        // candidate and wins whatever its build — there is nothing to fall
        // back to (the Ensure floor check still refuses to launch it if it
        // turns out unsupported).
        Assert.Equal(
            @"C:\external\llama.exe",
            LlamaManager.PreferManagedBinary(@"C:\external\llama.exe", null, "llama-server (llama) b12000 (abcdef)"));
    }

    [Fact]
    public void PreferManagedBinary_NoExternalBinary_UsesManaged()
    {
        Assert.Equal(
            @"C:\managed\llama.exe",
            LlamaManager.PreferManagedBinary(null, @"C:\managed\llama.exe", null));
    }

    [Fact]
    public void PreferManagedBinary_NothingFound_StaysNull()
    {
        Assert.Null(LlamaManager.PreferManagedBinary(null, null, null));
    }
}
