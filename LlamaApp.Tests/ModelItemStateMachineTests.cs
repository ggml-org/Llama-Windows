using LlamaApp.Common;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the <see cref="ModelItem"/> row state machine — the derived
/// glyph-visibility properties that drive the Available-row action cell
/// (play / download ring / load ring / open / failure affordance), the
/// download-progress captions, the change notifications, and the display-name
/// / server-id helpers.
///
/// These only touch pure managed logic: no XAML objects (SvgImageSource etc.)
/// are instantiated, so they run in a plain test host.
/// </summary>
public class ModelItemStateMachineTests
{
    // ----- Glyph visibility state machine ----------------------------------

    [Fact]
    public void Default_State_Shows_Play_Glyph()
    {
        var item = new ModelItem();

        Assert.True(item.PlayGlyphVisible);
        Assert.False(item.ProgressRingVisible);
        Assert.False(item.LoadingRingVisible);
        Assert.False(item.OpenGlyphVisible);
    }

    [Fact]
    public void Downloading_Shows_Progress_Ring_And_Hides_Play()
    {
        var item = new ModelItem { IsDownloading = true };

        Assert.True(item.ProgressRingVisible);
        Assert.False(item.PlayGlyphVisible);
        Assert.False(item.LoadingRingVisible);
        Assert.False(item.OpenGlyphVisible);
    }

    [Fact]
    public void Loading_Shows_Loading_Ring()
    {
        var item = new ModelItem { IsLoading = true };

        Assert.True(item.LoadingRingVisible);
        Assert.False(item.PlayGlyphVisible);
        Assert.False(item.ProgressRingVisible);
        Assert.False(item.OpenGlyphVisible);
    }

    [Fact]
    public void Download_Takes_Priority_Over_Load()
    {
        // A row that's mid-download shows the download ring even if a load
        // request is also in flight (a model can't load before it's cached).
        var item = new ModelItem { IsDownloading = true, IsLoading = true };

        Assert.True(item.ProgressRingVisible);
        Assert.False(item.LoadingRingVisible);
    }

    [Fact]
    public void Loaded_Shows_Open_Glyph()
    {
        var item = new ModelItem { IsLoaded = true };

        Assert.True(item.OpenGlyphVisible);
        Assert.False(item.PlayGlyphVisible);
        Assert.False(item.ProgressRingVisible);
        Assert.False(item.LoadingRingVisible);
    }

    [Fact]
    public void Failed_Download_Hides_Play_Glyph()
    {
        // Regression: a failed row must show the warning + retry affordance,
        // not the play glyph — the model isn't fully cached, so loading it
        // would just be rejected by the server.
        var item = new ModelItem { DownloadFailed = true };

        Assert.False(item.PlayGlyphVisible);

        item.DownloadFailed = false;
        Assert.True(item.PlayGlyphVisible);
    }

    // ----- Cancel-download button visibility -------------------------------

    [Fact]
    public void Cancel_Button_Hidden_When_Idle()
    {
        var item = new ModelItem();
        Assert.False(item.CancelDownloadVisible);
    }

    [Fact]
    public void Cancel_Button_Shown_Only_For_App_Driven_Downloads()
    {
        // An externally-triggered download (WebUI / CLI) has no cancellation
        // source — the button must hide rather than offer a no-op cancel.
        var item = new ModelItem { IsDownloading = true };
        Assert.False(item.CancelDownloadVisible);

        // The driver assigns the source when the download starts...
        item.DownloadCancellation = new CancellationTokenSource();
        Assert.True(item.CancelDownloadVisible);

        // ...and clears it when the download ends (before IsDownloading flips).
        item.DownloadCancellation = null;
        Assert.False(item.CancelDownloadVisible);
    }

    [Fact]
    public void Cancel_Button_Hides_When_Download_Ends()
    {
        var item = new ModelItem
        {
            IsDownloading = true,
            DownloadCancellation = new CancellationTokenSource(),
        };
        Assert.True(item.CancelDownloadVisible);

        item.IsDownloading = false;
        Assert.False(item.CancelDownloadVisible);
    }

    [Fact]
    public void DownloadCancellation_Raises_Cancel_Visibility_Notification()
    {
        var item = new ModelItem { IsDownloading = true };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.DownloadCancellation = new CancellationTokenSource();
        Assert.Contains(nameof(ModelItem.CancelDownloadVisible), raised);

        raised.Clear();
        item.DownloadCancellation = null;
        Assert.Contains(nameof(ModelItem.CancelDownloadVisible), raised);
    }

    [Fact]
    public void IsDownloading_Raises_Cancel_Visibility_Notification()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsDownloading = true;
        Assert.Contains(nameof(ModelItem.CancelDownloadVisible), raised);
    }

    // ----- Download progress captions --------------------------------------

    [Fact]
    public void Download_Is_Indeterminate_Until_First_Bytes()
    {
        var item = new ModelItem { IsDownloading = true };

        Assert.True(item.IsIndeterminateDownload);
        Assert.False(item.DownloadPercentTextVisible);

        item.DownloadFraction = 0.5;

        Assert.False(item.IsIndeterminateDownload);
        Assert.True(item.DownloadPercentTextVisible);
    }

    [Fact]
    public void Progress_Percent_And_Text_Are_Derived_From_Fraction()
    {
        var item = new ModelItem { DownloadFraction = 0.423 };

        Assert.Equal(42.3, item.DownloadProgressPercent, precision: 3);
        Assert.Equal("42%", item.DownloadProgressText);
    }

    [Fact]
    public void Percent_Caption_Hides_When_Not_Downloading()
    {
        var item = new ModelItem { DownloadFraction = 0.5 };

        Assert.False(item.DownloadPercentTextVisible); // not downloading
    }

    // ----- Load progress captions -------------------------------------------

    [Fact]
    public void Load_Is_Indeterminate_Until_First_Progress_Event()
    {
        var item = new ModelItem { IsLoading = true };

        Assert.True(item.IsIndeterminateLoad);
        Assert.False(item.LoadPercentTextVisible);

        item.LoadFraction = 0.5;

        Assert.False(item.IsIndeterminateLoad);
        Assert.True(item.LoadPercentTextVisible);
    }

    [Fact]
    public void Load_Percent_And_Text_Are_Derived_From_Fraction()
    {
        var item = new ModelItem { LoadFraction = 0.423 };

        Assert.Equal(42.3, item.LoadProgressPercent, precision: 3);
        Assert.Equal("42%", item.LoadProgressText);
    }

    [Fact]
    public void Load_Percent_Caption_Hides_When_Not_Loading()
    {
        var item = new ModelItem { LoadFraction = 0.5 };

        Assert.False(item.LoadPercentTextVisible); // not loading
    }

    [Fact]
    public void IsLoading_Raises_Load_Progress_Notifications()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsLoading = true;

        // The ring swaps indeterminate ↔ determinate on this transition.
        Assert.Contains(nameof(ModelItem.IsIndeterminateLoad), raised);
        Assert.Contains(nameof(ModelItem.LoadPercentTextVisible), raised);
    }

    // ----- Change notifications --------------------------------------------

    [Fact]
    public void State_Changes_Raise_Derived_Property_Notifications()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsDownloading = true;

        Assert.Contains(nameof(ModelItem.IsDownloading), raised);
        Assert.Contains(nameof(ModelItem.PlayGlyphVisible), raised);
        Assert.Contains(nameof(ModelItem.ProgressRingVisible), raised);
        Assert.Contains(nameof(ModelItem.LoadingRingVisible), raised);
        Assert.Contains(nameof(ModelItem.OpenGlyphVisible), raised);
        Assert.Contains(nameof(ModelItem.IsIndeterminateDownload), raised);
        Assert.Contains(nameof(ModelItem.DownloadPercentTextVisible), raised);
    }

    [Fact]
    public void DownloadFailed_Raises_Play_Glyph_Notification()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.DownloadFailed = true;

        // The UI swaps play ↔ warning+retry on this transition.
        Assert.Contains(nameof(ModelItem.PlayGlyphVisible), raised);
    }

    [Fact]
    public void No_Op_Assignments_Do_Not_Raise()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsLoaded = false;      // already false
        item.IsDownloading = false; // already false

        Assert.Empty(raised);
    }

    [Fact]
    public void Fraction_Noise_Below_Granularity_Does_Not_Raise()
    {
        // The setter guards at 0.001 granularity so a high-frequency progress
        // stream doesn't flood the UI with no-op updates.
        var item = new ModelItem { DownloadFraction = 0.5 };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.DownloadFraction = 0.5005; // below the 0.001 guard
        Assert.Empty(raised);

        item.DownloadFraction = 0.502; // above it
        Assert.Contains(nameof(ModelItem.DownloadProgressPercent), raised);
        Assert.Contains(nameof(ModelItem.DownloadProgressText), raised);
    }

    // ----- Display name / server id helpers --------------------------------

    [Theory]
    [InlineData("ggml-org/gpt-oss-20b-GGUF", "gpt-oss-20b-GGUF")]
    [InlineData("GPT-OSS 20B (mxfp4)", "GPT-OSS 20B (mxfp4)")]
    [InlineData("a/b/c", "c")]
    public void DisplayName_Takes_Last_Path_Segment(string name, string expected)
    {
        var item = new ModelItem { Name = name };
        Assert.Equal(expected, item.DisplayName);
    }

    [Fact]
    public void IModel_Name_Prefers_Repo_Name_Over_Display_Name()
    {
        var item = new ModelItem { Name = "GPT-OSS 20B (mxfp4)", RepoName = "ggml-org/gpt-oss-20b-GGUF" };
        Assert.Equal("ggml-org/gpt-oss-20b-GGUF", ((IModel)item).Name);

        var fallback = new ModelItem { Name = "some/name" };
        Assert.Equal("some/name", ((IModel)fallback).Name);
    }

    [Fact]
    public void Server_Model_Id_Appends_Quant_When_Present()
    {
        var withQuant = new ModelItem { RepoName = "ggml-org/gpt-oss-20b-GGUF", Quant = "mxfp4" };
        Assert.Equal("ggml-org/gpt-oss-20b-GGUF:mxfp4", ((IModel)withQuant).ServerModelId);

        var withoutQuant = new ModelItem { RepoName = "ggml-org/gpt-oss-20b-GGUF" };
        Assert.Equal("ggml-org/gpt-oss-20b-GGUF", ((IModel)withoutQuant).ServerModelId);
    }

    // ----- Logo resolution (null paths only — no SvgImageSource created) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-brand")]
    public void ResolveLogo_Returns_Null_For_Null_Or_Unknown_Brand(string? brand)
    {
        Assert.Null(ModelItem.ResolveLogo(brand));
    }
}
