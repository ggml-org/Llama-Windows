using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for <see cref="LlamaManager.BuildPresetsIni"/> — the
/// model-presets INI passed to the llama server via <c>--models-preset</c>.
/// The format matters: the router refuses to start on a malformed file, and
/// each section must be keyed by the full server model id
/// (<c>repo:quant</c>) to match the model's child-process launch.
/// </summary>
public sealed class PresetsIniTests
{
    [Fact]
    public void Empty_map_renders_an_empty_file()
    {
        Assert.Equal("", LlamaManager.BuildPresetsIni(new Dictionary<string, int>()));
    }

    [Fact]
    public void Each_entry_renders_a_section_with_its_ctx_size()
    {
        var ini = LlamaManager.BuildPresetsIni(new Dictionary<string, int>
        {
            ["ggml-org/gpt-oss-20b-GGUF:mxfp4"] = 8192,
        });

        Assert.Contains("[ggml-org/gpt-oss-20b-GGUF:mxfp4]", ini);
        Assert.Contains("ctx-size = 8192", ini);
    }

    [Fact]
    public void Non_positive_sizes_are_skipped()
    {
        // 0 means "model default" — no preset section for it.
        var ini = LlamaManager.BuildPresetsIni(new Dictionary<string, int>
        {
            ["repo/a:Q4_0"] = 0,
            ["repo/b:Q4_0"] = -5,
            ["repo/c:Q4_0"] = 4096,
        });

        Assert.DoesNotContain("repo/a", ini);
        Assert.DoesNotContain("repo/b", ini);
        Assert.Contains("[repo/c:Q4_0]", ini);
    }

    [Fact]
    public void Sections_are_sorted_for_stable_output()
    {
        var sizes = new Dictionary<string, int>
        {
            ["repo/z:Q4_0"] = 4096,
            ["repo/a:Q4_0"] = 8192,
            ["repo/m:Q4_0"] = 16384,
        };

        var ini = LlamaManager.BuildPresetsIni(sizes);

        Assert.True(ini.IndexOf("[repo/a:Q4_0]", StringComparison.Ordinal) <
                    ini.IndexOf("[repo/m:Q4_0]", StringComparison.Ordinal));
        Assert.True(ini.IndexOf("[repo/m:Q4_0]", StringComparison.Ordinal) <
                    ini.IndexOf("[repo/z:Q4_0]", StringComparison.Ordinal));
        // Stable: same input → identical bytes (the file is rewritten often).
        Assert.Equal(ini, LlamaManager.BuildPresetsIni(sizes));
    }
}
