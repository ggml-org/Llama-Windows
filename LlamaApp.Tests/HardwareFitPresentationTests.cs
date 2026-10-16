using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the fit-level presentation
/// (<see cref="HardwareFitPresentation"/>) — the green/yellow/red mapping
/// of the three <see cref="HardwareFitLevel"/> verdicts and the short
/// spoken names that carry the level where color can't reach (the visible
/// UI shows the badge only). Colors are plain structs and stay testable;
/// the badges built from the SVG assets are XAML objects and never
/// instantiate here (the test host runs without the Windows App SDK
/// runtime — see LlamaApp.Tests.csproj), mirroring
/// <see cref="QuantizationPresentationTests"/>.
/// </summary>
public class HardwareFitPresentationTests
{
    [Theory]
    [InlineData(HardwareFitLevel.Fit, "Fits")]
    [InlineData(HardwareFitLevel.Might, "Might fit")]
    [InlineData(HardwareFitLevel.NoWay, "Won't fit")]
    public void Every_Level_Has_A_Short_Spoken_Name(HardwareFitLevel level, string name)
    {
        Assert.Equal(name, HardwareFitPresentation.Label(level));
    }

    [Theory]
    [InlineData(HardwareFitLevel.Fit)]
    [InlineData(HardwareFitLevel.Might)]
    [InlineData(HardwareFitLevel.NoWay)]
    public void Every_Level_Maps_To_Its_Own_Color(HardwareFitLevel level)
    {
        Assert.Equal(HardwareFitPresentation.ColorFor(level), level switch
        {
            HardwareFitLevel.Fit => HardwareFitPresentation.FitColor,
            HardwareFitLevel.Might => HardwareFitPresentation.MightColor,
            _ => HardwareFitPresentation.NoWayColor,
        });
    }

    [Fact]
    public void The_Three_Levels_Are_Green_Yellow_Red()
    {
        // Distinct hues — a row's level has to be readable at a glance.
        Assert.NotEqual(HardwareFitPresentation.FitColor, HardwareFitPresentation.MightColor);
        Assert.NotEqual(HardwareFitPresentation.MightColor, HardwareFitPresentation.NoWayColor);
        Assert.NotEqual(HardwareFitPresentation.FitColor, HardwareFitPresentation.NoWayColor);

        // …and each hue is its color: green with the dominant green
        // channel, yellow (red over green over blue), red with the dominant
        // red channel.
        Assert.True(HardwareFitPresentation.FitColor.G > HardwareFitPresentation.FitColor.R);
        Assert.True(HardwareFitPresentation.FitColor.G > HardwareFitPresentation.FitColor.B);

        Assert.True(HardwareFitPresentation.MightColor.R > HardwareFitPresentation.MightColor.G);
        Assert.True(HardwareFitPresentation.MightColor.G > HardwareFitPresentation.MightColor.B);

        Assert.True(HardwareFitPresentation.NoWayColor.R > HardwareFitPresentation.NoWayColor.G);
        Assert.True(HardwareFitPresentation.NoWayColor.R > HardwareFitPresentation.NoWayColor.B);
    }
}
