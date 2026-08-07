using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the Recommended-row download options: the expander state
/// (chevron), the quantization picker (<see cref="ModelItem.SelectedBuild"/>
/// mirroring the chosen build into the server id / subtitle / preflight
/// inputs), and the context-size picker mapping.
/// </summary>
public sealed class ModelItemOptionsTests
{
    private static QuantOption Build(string quant, string size, ulong bytes) =>
        new() { Quant = quant, SizeLabel = size, SizeBytes = bytes };

    // ----- Expander ---------------------------------------------------------

    [Fact]
    public void Chevron_points_right_when_collapsed_down_when_expanded()
    {
        var item = new ModelItem();
        Assert.Equal("\uE76C", item.ChevronGlyph);

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        item.IsExpanded = true;

        Assert.Equal("\uE70D", item.ChevronGlyph);
        Assert.Contains(nameof(ModelItem.IsExpanded), raised);
        Assert.Contains(nameof(ModelItem.ChevronGlyph), raised);
    }

    [Fact]
    public void Row_surface_is_transparent_when_collapsed_and_notifies_on_expand()
    {
        // Default static brush is null, so the surface is transparent either
        // way here — what matters is the re-notification on toggle (the shell
        // swaps SubtleFillBrush per theme).
        var item = new ModelItem();
        Assert.Null(item.RowSurface);

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        item.IsExpanded = true;

        Assert.Contains(nameof(ModelItem.RowSurface), raised);
    }

    // ----- Quantization picker ----------------------------------------------

    [Fact]
    public void Selecting_a_build_mirrors_quant_size_and_bytes()
    {
        var item = new ModelItem { RepoName = "ggml-org/gpt-oss-20b-GGUF" };
        var mxfp4 = Build("mxfp4", "12.1 GB", 12_100_000_000);
        var q4 = Build("Q4_0", "11 GB", 11_000_000_000);
        item.Builds = [mxfp4, q4];

        item.SelectedBuild = mxfp4;
        Assert.Equal("mxfp4", item.Quant);
        Assert.Equal("12.1 GB", item.Size);
        Assert.Equal(12_100_000_000UL, item.SizeBytes);
        Assert.Equal("ggml-org/gpt-oss-20b-GGUF:mxfp4", ((Common.IModel)item).ServerModelId);

        item.SelectedBuild = q4;
        Assert.Equal("Q4_0", item.Quant);
        Assert.Equal("11 GB", item.Size);
        Assert.Equal(11_000_000_000UL, item.SizeBytes);
        Assert.Equal("ggml-org/gpt-oss-20b-GGUF:Q4_0", ((Common.IModel)item).ServerModelId);
    }

    [Fact]
    public void Selecting_a_build_notifies_size_and_subtitle()
    {
        var item = new ModelItem { Parameters = "20B" };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.SelectedBuild = Build("Q4_0", "11 GB", 1);

        Assert.Contains(nameof(ModelItem.SelectedBuild), raised);
        Assert.Contains(nameof(ModelItem.Size), raised);
        Assert.Contains(nameof(ModelItem.SubtitleText), raised);
        Assert.Equal("20B · 11 GB", item.SubtitleText);
    }

    [Fact]
    public void Null_selection_keeps_the_last_build()
    {
        // A TwoWay ComboBox binding pushes null when the selection clears
        // (e.g. ItemsSource swap) — that must not blank the row's server id.
        var item = new ModelItem { RepoName = "repo" };
        item.SelectedBuild = Build("mxfp4", "12 GB", 2);

        item.SelectedBuild = null;

        Assert.Equal("mxfp4", item.Quant);
        Assert.Equal("repo:mxfp4", ((Common.IModel)item).ServerModelId);
    }

    [Fact]
    public void Quant_option_label_pairs_quant_and_size()
    {
        Assert.Equal("Q4_K_M · 4.2 GB", Build("Q4_K_M", "4.2 GB", 0).Label);
        Assert.Equal("mxfp4", Build("mxfp4", "", 0).Label); // no size → bare quant
    }

    // ----- Context-size picker ----------------------------------------------

    [Fact]
    public void Default_context_size_is_model_default()
    {
        var item = new ModelItem();
        Assert.Equal(0, item.SelectedContextSize.CtxSize);
        Assert.Equal("Model default", item.SelectedContextSize.Label);
    }

    [Theory]
    [InlineData(4_096, "4K")]
    [InlineData(32_768, "32K")]
    [InlineData(131_072, "128K")]
    public void Context_size_option_for_maps_tokens_to_the_picker_entry(int tokens, string label)
    {
        var opt = ModelItem.ContextSizeOptionFor(tokens);
        Assert.Equal(tokens, opt.CtxSize);
        Assert.Equal(label, opt.Label);
    }

    [Fact]
    public void Context_size_option_for_falls_back_to_default_for_unknown_values()
    {
        Assert.Equal(0, ModelItem.ContextSizeOptionFor(0).CtxSize);
        Assert.Equal(0, ModelItem.ContextSizeOptionFor(999).CtxSize);
    }

    [Fact]
    public void Every_row_picker_offers_the_same_shared_options()
    {
        var a = new ModelItem();
        var b = new ModelItem();
        Assert.Same(a.ContextSizeOptions, b.ContextSizeOptions);
        Assert.Equal(7, a.ContextSizeOptions.Count);
    }
}
