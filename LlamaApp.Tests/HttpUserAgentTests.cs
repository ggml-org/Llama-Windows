using System.Text.RegularExpressions;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Contract for the shared HTTP User-Agent every client must attach:
/// <c>LlamaWindows/&lt;major.minor.patch&gt;</c> from the entry assembly's
/// version, stable for the lifetime of the process.
/// </summary>
public class HttpUserAgentTests
{
    [Fact]
    public void Value_is_LlamaWindows_with_a_three_part_version()
    {
        Assert.Matches(new Regex(@"^LlamaWindows/\d+\.\d+\.\d+$"), HttpUserAgent.Value);
    }

    [Fact]
    public void Value_is_stable_across_calls()
    {
        Assert.Equal(HttpUserAgent.Value, HttpUserAgent.Value);
    }

    [Fact]
    public void Value_is_not_the_old_bare_Llama_literal()
    {
        // The pre-telemetry User-Agent — make sure nobody reintroduces it.
        Assert.NotEqual("Llama/1.0", HttpUserAgent.Value);
    }
}
