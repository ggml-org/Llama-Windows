using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the <see cref="ModelItem"/> device-fit presentation state —
/// the dimming of Recommended rows the machine can't run
/// (<see cref="ModelItem.FitsOnDevice"/> → <see cref="ModelItem.RowOpacity"/>)
/// and the tooltip line that explains why (<see cref="ModelItem.FitNote"/>).
/// Pure managed logic only, like <see cref="ModelItemStateMachineTests"/>.
/// </summary>
public class ModelItemFitPresentationTests
{
    [Fact]
    public void Default_Fits_And_Renders_Full_Strength()
    {
        var item = new ModelItem();

        Assert.True(item.FitsOnDevice);
        Assert.Null(item.FitNote);
        Assert.Equal(1.0, item.RowOpacity);
    }

    [Fact]
    public void Not_Fitting_Dims_The_Row()
    {
        var item = new ModelItem { FitsOnDevice = false };

        Assert.Equal(0.4, item.RowOpacity);
    }

    [Fact]
    public void Fit_Note_Is_Appended_To_The_Tooltip()
    {
        var item = new ModelItem
        {
            Name = "GPT-OSS 20B (mxfp4)",
            Description = "An open-weight reasoning model.",
            FitsOnDevice = false,
            FitNote = "May not fit: needs about 14.2 GB.",
        };

        Assert.Equal(
            "GPT-OSS 20B (mxfp4)\nAn open-weight reasoning model.\nMay not fit: needs about 14.2 GB.",
            item.RowToolTip);
    }

    [Fact]
    public void Tooltip_Falls_Back_To_Name_And_Drops_Empty_Note()
    {
        var item = new ModelItem
        {
            Name = "some-org/some-model",
            Description = "",
            FitNote = null,
        };

        Assert.Equal("some-org/some-model", item.RowToolTip);
    }

    [Fact]
    public void FitsOnDevice_Change_Notifies_Opacity_And_Tooltip()
    {
        var item = new ModelItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.FitsOnDevice = false;

        Assert.Contains("FitsOnDevice", changed);
        Assert.Contains("RowOpacity", changed);
        Assert.Contains("RowToolTip", changed);
    }

    [Fact]
    public void Same_Value_Does_Not_Notify()
    {
        var item = new ModelItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.FitsOnDevice = true; // already the default

        Assert.Empty(changed);
    }

    [Fact]
    public void Fit_Note_Change_Notifies_Tooltip()
    {
        var item = new ModelItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.FitNote = "May not fit: needs about 14.2 GB.";

        Assert.Contains("FitNote", changed);
        Assert.Contains("RowToolTip", changed);
    }
}
