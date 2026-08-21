using LlamaApp.Common;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the <see cref="ModelItem"/> row state machine — the derived
/// glyph-visibility properties that drive the Available-row action cell
/// (play / download ring / load ring / open / pause-resume / failure
/// affordance), the download-progress captions, the change notifications,
/// and the display-name / server-id helpers.
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

    // ----- Delete (bin) button visibility ----------------------------------

    [Fact]
    public void Delete_Button_Shown_For_An_Idle_Removable_Row()
    {
        var item = new ModelItem();
        Assert.True(item.DeleteGlyphVisible);
    }

    [Fact]
    public void Delete_Button_Hidden_When_The_Server_Says_Not_Removable()
    {
        // can_remove=false (preset / --models-dir source): the router refuses
        // DELETE for those — offering the bin icon was a guaranteed silent
        // no-op ("the delete button does nothing").
        var item = new ModelItem { CanRemove = false };
        Assert.False(item.DeleteGlyphVisible);
        Assert.True(item.PlayGlyphVisible); // the row itself stays normal
    }

    [Fact]
    public void Delete_Button_Follows_The_Play_Glyph_States()
    {
        // Every state that hides the play glyph hides the bin too.
        var item = new ModelItem { IsLoaded = true };
        Assert.False(item.DeleteGlyphVisible);

        item = new ModelItem { IsLoading = true };
        Assert.False(item.DeleteGlyphVisible);

        item = new ModelItem { IsDownloading = true };
        Assert.False(item.DeleteGlyphVisible);

        item = new ModelItem { DownloadFailed = true };
        Assert.False(item.DeleteGlyphVisible);
    }

    [Fact]
    public void CanRemove_Raises_Delete_Visibility_Notification()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.CanRemove = false;
        Assert.Contains(nameof(ModelItem.DeleteGlyphVisible), raised);
    }

    [Fact]
    public void State_Changes_Raise_Delete_Visibility_Notification()
    {
        // The bin shares the play glyph's visibility inputs — each must
        // re-notify DeleteGlyphVisible or the button lags one state behind.
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsLoaded = true;
        Assert.Contains(nameof(ModelItem.DeleteGlyphVisible), raised);
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

    // ----- Pause / resume state machine ------------------------------------

    [Fact]
    public void Pausing_A_Download_Swaps_The_Ring_For_The_Resume_Glyph()
    {
        // The pause click sets DownloadPaused and cancels; the driver's
        // cancellation unwind then clears IsDownloading (see
        // MainWindow.LocalModelPauseDownload_Click / DownloadAndLaunchAsync).
        var item = new ModelItem
        {
            IsDownloading = true,
            DownloadCancellation = new CancellationTokenSource(),
        };

        item.DownloadPaused = true;       // pause click
        item.IsDownloading = false;       // cancellation unwound
        item.DownloadCancellation = null; // driver's finally

        Assert.True(item.ResumeDownloadVisible);
        Assert.False(item.ProgressRingVisible);
        Assert.False(item.PlayGlyphVisible);
        Assert.True(item.CancelDownloadVisible); // abandon affordance stays
    }

    [Fact]
    public void Resume_Glyph_Hidden_When_Idle()
    {
        var item = new ModelItem();
        Assert.False(item.ResumeDownloadVisible);
    }

    [Fact]
    public void Resume_Glyph_Hides_While_The_Pause_Is_Racing_The_Download()
    {
        // Transitional state: the pause click set DownloadPaused but the
        // cancellation hasn't unwound yet — the ring is still up, so the
        // resume glyph must not double up in the same slot.
        var item = new ModelItem
        {
            IsDownloading = true,
            DownloadCancellation = new CancellationTokenSource(),
            DownloadPaused = true,
        };

        Assert.False(item.ResumeDownloadVisible);
        Assert.True(item.ProgressRingVisible);
    }

    [Theory]
    [InlineData(true, false, false)]  // load ring up
    [InlineData(false, true, false)]  // model loaded
    [InlineData(false, false, true)]  // failure affordance up
    public void Resume_Glyph_Hides_During_Transitional_States(bool isLoading, bool isLoaded, bool downloadFailed)
    {
        // A completion racing the pause could otherwise leave the resume
        // glyph up next to the load ring or the failure pair.
        var item = new ModelItem
        {
            DownloadPaused = true,
            IsLoading = isLoading,
            IsLoaded = isLoaded,
            DownloadFailed = downloadFailed,
        };

        Assert.False(item.ResumeDownloadVisible);
    }

    [Fact]
    public void Pause_Requires_An_App_Driven_Download_In_Flight()
    {
        var item = new ModelItem();
        Assert.False(item.CanPauseDownload); // idle

        // An externally-triggered download (WebUI / CLI) has no cancellation
        // source — the ring's pause button disables rather than offering a
        // no-op pause (same rule as the cancel button).
        item.IsDownloading = true;
        Assert.False(item.CanPauseDownload);

        item.DownloadCancellation = new CancellationTokenSource();
        Assert.True(item.CanPauseDownload);

        item.IsDownloading = false;
        Assert.False(item.CanPauseDownload);
    }

    [Fact]
    public void CanPauseDownload_Raises_Notifications_On_Both_Inputs()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsDownloading = true;
        Assert.Contains(nameof(ModelItem.CanPauseDownload), raised);

        raised.Clear();
        item.DownloadCancellation = new CancellationTokenSource();
        Assert.Contains(nameof(ModelItem.CanPauseDownload), raised);
    }

    [Fact]
    public void Cancel_Button_Stays_Visible_While_Paused()
    {
        // Paused: the cancel button abandons the partial download (the server
        // side is already stopped), so it must not hide with the ring.
        var item = new ModelItem
        {
            IsDownloading = true,
            DownloadCancellation = new CancellationTokenSource(),
        };

        item.DownloadPaused = true;
        item.IsDownloading = false;
        item.DownloadCancellation = null;

        Assert.True(item.CancelDownloadVisible);

        // Abandoning the partial returns the row to idle — the button hides.
        item.DownloadPaused = false;
        Assert.False(item.CancelDownloadVisible);
    }

    [Fact]
    public void Paused_Percent_Caption_Shows_The_Frozen_Completion()
    {
        var item = new ModelItem
        {
            IsDownloading = true,
            DownloadFraction = 0.42,
        };

        item.DownloadPaused = true;
        item.IsDownloading = false;

        Assert.True(item.PausedPercentTextVisible);
        Assert.Equal("42%", item.DownloadProgressText);
        // The ring's live caption hides with the ring.
        Assert.False(item.DownloadPercentTextVisible);
    }

    [Fact]
    public void Paused_Percent_Caption_Hides_Without_Known_Progress()
    {
        // Paused before the first byte count arrived — no "0%" caption.
        var item = new ModelItem { DownloadPaused = true };
        Assert.False(item.PausedPercentTextVisible);
    }

    [Fact]
    public void Paused_Subtitle_Shows_The_Frozen_Byte_Counts()
    {
        var item = new ModelItem
        {
            Parameters = "20B",
            Size = "12.1 GB",
            IsDownloading = true,
            DownloadedBytes = 3_200_000_000,
            DownloadTotalBytes = 12_100_000_000,
        };

        item.DownloadPaused = true;
        item.IsDownloading = false;

        Assert.Equal("Paused · 3.2 GB of 12.1 GB", item.SubtitleText);
    }

    [Fact]
    public void Paused_Subtitle_Falls_Back_To_Params_Size_When_Size_Unknown()
    {
        // Paused before the server reported a size — no "Paused · 0 B of 0 B".
        var item = new ModelItem
        {
            Parameters = "20B",
            Size = "12.1 GB",
            DownloadPaused = true,
        };

        Assert.Equal("20B · 12.1 GB", item.SubtitleText);
    }

    [Fact]
    public void DownloadPaused_Raises_Derived_Property_Notifications()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.DownloadPaused = true;

        Assert.Contains(nameof(ModelItem.DownloadPaused), raised);
        Assert.Contains(nameof(ModelItem.PlayGlyphVisible), raised);
        Assert.Contains(nameof(ModelItem.ResumeDownloadVisible), raised);
        Assert.Contains(nameof(ModelItem.CancelDownloadVisible), raised);
        Assert.Contains(nameof(ModelItem.PausedPercentTextVisible), raised);
        Assert.Contains(nameof(ModelItem.SubtitleText), raised);
    }

    [Fact]
    public void Resuming_Restarts_The_Download_Ring()
    {
        // The resume click clears DownloadPaused and re-enters the download
        // driver, which reassigns the cancellation source (see
        // MainWindow.LocalModelResumeDownload_Click / DownloadAndLaunchAsync).
        var item = new ModelItem
        {
            DownloadPaused = true,
            DownloadFraction = 0.42,
            DownloadedBytes = 3_200_000_000,
            DownloadTotalBytes = 12_100_000_000,
        };

        item.DownloadPaused = false;
        item.IsDownloading = true;
        item.DownloadCancellation = new CancellationTokenSource();

        Assert.True(item.ProgressRingVisible);
        Assert.False(item.ResumeDownloadVisible);
        Assert.True(item.CanPauseDownload);
        Assert.False(item.PlayGlyphVisible);
    }

    [Fact]
    public void Abandoning_A_Paused_Download_Returns_The_Row_To_Play()
    {
        // Cancel while paused (see MainWindow.LocalModelCancelDownload_Click):
        // the partial is abandoned and every download counter resets.
        var item = new ModelItem
        {
            Parameters = "20B",
            Size = "12.1 GB",
            DownloadPaused = true,
            DownloadFraction = 0.42,
            DownloadedBytes = 3_200_000_000,
            DownloadTotalBytes = 12_100_000_000,
            DownloadBytesPerSecond = 45_000_000,
        };

        item.DownloadPaused = false;
        item.DownloadFraction = 0;
        item.DownloadedBytes = 0;
        item.DownloadTotalBytes = 0;
        item.DownloadBytesPerSecond = 0;

        Assert.True(item.PlayGlyphVisible);
        Assert.False(item.ResumeDownloadVisible);
        Assert.False(item.CancelDownloadVisible);
        Assert.False(item.PausedPercentTextVisible);
        Assert.Equal("20B · 12.1 GB", item.SubtitleText);
    }

    [Fact]
    public void Completion_Discards_A_Racing_Pause()
    {
        // The pause click landed just as the download finished — the driver's
        // completion path clears DownloadPaused (nothing left to resume) and
        // moves on to loading.
        var item = new ModelItem
        {
            IsDownloading = true,
            DownloadCancellation = new CancellationTokenSource(),
            DownloadPaused = true,
        };

        item.IsDownloading = false;
        item.DownloadPaused = false;
        item.IsLoading = true;

        Assert.False(item.ResumeDownloadVisible);
        Assert.True(item.LoadingRingVisible);
    }

    // ----- Load failure affordance -----------------------------------------

    [Fact]
    public void Failed_Load_Hides_Play_Glyph()
    {
        // A rejected load must show the warning + retry affordance, not the
        // play glyph — otherwise the failure is silent (the ring just vanishes).
        var item = new ModelItem { LoadFailed = true };

        Assert.False(item.PlayGlyphVisible);
        Assert.False(item.LoadingRingVisible);
        Assert.False(item.OpenGlyphVisible);

        item.LoadFailed = false;
        Assert.True(item.PlayGlyphVisible);
    }

    [Fact]
    public void LoadFailed_Raises_Play_Glyph_Notification()
    {
        var item = new ModelItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.LoadFailed = true;

        // The UI swaps play ↔ warning+retry on this transition.
        Assert.Contains(nameof(ModelItem.LoadFailed), raised);
        Assert.Contains(nameof(ModelItem.PlayGlyphVisible), raised);
    }

    [Fact]
    public void Loading_Again_After_Failure_Shows_Ring_Not_Affordance()
    {
        // The retry path clears LoadFailed and sets IsLoading: the load ring
        // shows and the play glyph stays hidden.
        var item = new ModelItem { LoadFailed = true };

        item.LoadFailed = false;
        item.IsLoading = true;

        Assert.True(item.LoadingRingVisible);
        Assert.False(item.PlayGlyphVisible);
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

    // ----- Download detail line / subtitle ---------------------------------

    [Fact]
    public void Subtitle_Is_Params_And_Size_At_Rest()
    {
        var item = new ModelItem { Parameters = "20B", Size = "12.1 GB" };
        Assert.Equal("20B · 12.1 GB", item.SubtitleText);
    }

    [Fact]
    public void Subtitle_Drops_Empty_Parts()
    {
        // An uncataloged model can lack params/size — no dangling " · ".
        var item = new ModelItem { Parameters = "", Size = "12.1 GB" };
        Assert.Equal("12.1 GB", item.SubtitleText);
    }

    [Fact]
    public void Subtitle_Shows_Download_Detail_While_Downloading()
    {
        var item = new ModelItem
        {
            Parameters = "20B",
            Size = "12.1 GB",
            IsDownloading = true,
            DownloadedBytes = 3_200_000_000,
            DownloadTotalBytes = 12_100_000_000,
        };

        Assert.Equal("3.2 GB of 12.1 GB", item.SubtitleText);

        // Size unknown yet → the rest subtitle stays (no "0 B of 0 B").
        var early = new ModelItem { Parameters = "20B", Size = "12.1 GB", IsDownloading = true };
        Assert.Equal("20B · 12.1 GB", early.SubtitleText);
    }

    [Fact]
    public void Subtitle_Returns_To_Params_Size_When_Download_Ends()
    {
        var item = new ModelItem
        {
            Parameters = "20B",
            Size = "12.1 GB",
            IsDownloading = true,
            DownloadedBytes = 3_200_000_000,
            DownloadTotalBytes = 12_100_000_000,
        };

        item.IsDownloading = false;
        Assert.Equal("20B · 12.1 GB", item.SubtitleText);
    }

    [Fact]
    public void Byte_Updates_Raise_Subtitle_Notifications()
    {
        var item = new ModelItem { IsDownloading = true };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.DownloadTotalBytes = 12_100_000_000;
        item.DownloadedBytes = 3_200_000_000;
        item.DownloadBytesPerSecond = 45_000_000;

        Assert.Contains(nameof(ModelItem.SubtitleText), raised);
        Assert.Contains(nameof(ModelItem.DownloadDetailText), raised);
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

    // ----- Row tooltip (name + description) --------------------------------

    [Fact]
    public void RowToolTip_Is_The_Name_When_No_Description()
    {
        var item = new ModelItem { Name = "GPT-OSS 20B (mxfp4)" };
        Assert.Equal("GPT-OSS 20B (mxfp4)", item.RowToolTip);
    }

    [Fact]
    public void RowToolTip_Appends_The_Description_On_A_Second_Line()
    {
        // The catalog's description was populated but bound nowhere — the
        // tooltip is how you tell what a model is before downloading it.
        var item = new ModelItem
        {
            Name = "GPT-OSS 20B (mxfp4)",
            Description = "OpenAI's open-weight reasoning model",
        };
        Assert.Equal("GPT-OSS 20B (mxfp4)\nOpenAI's open-weight reasoning model", item.RowToolTip);
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
