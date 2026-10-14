using System.Text.RegularExpressions;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Contract for the shared HTTP User-Agent every client must attach:
/// <c>llama-win/<major.minor.patch> (<major.minor.build>; <arch>)</c>
/// — the app version from the entry assembly, the real Windows build, and
/// the <b>machine's native</b> architecture (not the process's: the app
/// ships an x64 build that runs emulated on ARM64 PCs, and a process-arch
/// token would report x64 for every ARM64 machine, hiding them from
/// backend statistics). This header is the only usage signal the app
/// emits (there is no telemetry), so its shape is pinned here.
/// </summary>
public class HttpUserAgentTests
{
    [Fact]
    public void Value_is_llama_win_version_with_windows_build_and_arch()
    {
        Assert.Matches(
            new Regex(@"^llama-win/\d+\.\d+\.\d+ \(\d+\.\d+\.\d+; (x64|x86|arm64|arm)\)$"),
            HttpUserAgent.Value);
    }

    [Fact]
    public void Value_reports_the_native_os_architecture()
    {
        // The arch token is exactly the native OS architecture (lowercased)
        // — on an ARM64 PC this is arm64 even though the process is x64
        // running under emulation.
        Assert.EndsWith(
            $"; {HttpUserAgent.NativeOsArchitecture().ToLowerInvariant()})",
            HttpUserAgent.Value);
    }

    [Fact]
    public void Value_is_stable_across_calls()
    {
        Assert.Equal(HttpUserAgent.Value, HttpUserAgent.Value);
    }

    [Fact]
    public void Value_is_not_one_of_the_old_user_agent_literals()
    {
        // Regression guards: the original bare "Llama/x" agent and the
        // telemetry-era "LlamaWindows/x.y.z" product — neither may return.
        Assert.DoesNotMatch(new Regex(@"^Llama/"), HttpUserAgent.Value);
        Assert.DoesNotMatch(new Regex(@"^LlamaWindows/"), HttpUserAgent.Value);
    }
}
