using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the determinate progress arc geometry math
/// (<see cref="ProgressArcPresentation.Compute"/>) — the row rings draw this
/// arc instead of WinUI's determinate ProgressRing (which paints a lighter
/// background disk behind the value arc). The arc starts at 12 o'clock on
/// the 20×20 ring (center 10,10; radius 9 = (20−2)/2) and sweeps clockwise.
/// </summary>
public class ProgressArcPresentationTests
{
    private const double Eps = 0.0001;

    [Fact]
    public void Quarter_Fraction_Ends_At_Three_OClock()
    {
        var (x, y, large, full) = ProgressArcPresentation.Compute(0.25);
        Assert.Equal(19, x, Eps); // center + radius
        Assert.Equal(10, y, Eps);
        Assert.False(large);
        Assert.False(full);
    }

    [Fact]
    public void Half_Fraction_Ends_At_Six_OClock_And_Is_Still_The_Small_Arc()
    {
        var (x, y, large, full) = ProgressArcPresentation.Compute(0.5);
        Assert.Equal(10, x, Eps);
        Assert.Equal(19, y, Eps); // center + radius
        Assert.False(large); // exactly half is not "large"
        Assert.False(full);
    }

    [Fact]
    public void Three_Quarter_Fraction_Ends_At_Nine_OClock_As_The_Large_Arc()
    {
        var (x, y, large, full) = ProgressArcPresentation.Compute(0.75);
        Assert.Equal(1, x, Eps); // center − radius
        Assert.Equal(10, y, Eps);
        Assert.True(large);
        Assert.False(full);
    }

    [Fact]
    public void Complete_Fraction_Is_The_Full_Ring()
    {
        var (_, _, _, full) = ProgressArcPresentation.Compute(1.0);
        Assert.True(full);
    }

    [Fact]
    public void Nearly_Complete_Fraction_Is_Treated_As_Full()
    {
        // An arc whose endpoints coincide would collapse — anything that
        // rounds to complete takes the ellipse path instead.
        var (_, _, _, full) = ProgressArcPresentation.Compute(0.9995);
        Assert.True(full);
    }

    [Fact]
    public void Fractions_Are_Clamped_To_The_Ring()
    {
        var (xOver, yOver, _, _) = ProgressArcPresentation.Compute(1.5);
        var (_, _, _, fullOver) = ProgressArcPresentation.Compute(1.5);
        Assert.True(fullOver);
        Assert.Equal(10, xOver, Eps); // full-ring anchor = 12 o'clock
        Assert.Equal(1, yOver, Eps);

        var (xNeg, yNeg, large, full) = ProgressArcPresentation.Compute(-0.2);
        Assert.False(full);
        Assert.False(large);
        Assert.Equal(10, xNeg, Eps); // zero sweep stays at 12 o'clock
        Assert.Equal(1, yNeg, Eps);
    }

    [Fact]
    public void Nan_Reads_As_Empty()
    {
        var (x, y, large, full) = ProgressArcPresentation.Compute(double.NaN);
        Assert.False(full);
        Assert.False(large);
        Assert.Equal(10, x, Eps);
        Assert.Equal(1, y, Eps);
    }

    [Fact]
    public void Arc_Returns_A_Geometry_For_Every_State()
    {
        // Smoke: the x:Bind target must never hand Path.Data a null.
        Assert.NotNull(ProgressArcPresentation.Arc(0));
        Assert.NotNull(ProgressArcPresentation.Arc(0.42));
        Assert.NotNull(ProgressArcPresentation.Arc(1));
        Assert.NotNull(ProgressArcPresentation.Arc(double.NaN));
    }
}
