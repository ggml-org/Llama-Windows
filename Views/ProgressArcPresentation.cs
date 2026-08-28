using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LlamaApp.Views;

/// <summary>
/// Geometry for the determinate progress arc shown in the model-row rings
/// (download and load). Drawn as a bare <see cref="System.Windows.Shapes.Path"/>
/// instead of WinUI's <see cref="Microsoft.UI.Xaml.Controls.ProgressRing"/> in
/// determinate mode: the control paints a lighter background disk behind the
/// value arc, which read as a stray blob behind the 20px row rings. A custom
/// arc renders only the arc itself on the row's transparent surface. The
/// indeterminate spin still uses the ProgressRing (no background there).
///
/// <para>The arc starts at 12 o'clock and sweeps clockwise, matching the
/// ProgressRing's direction; round caps on both ends. A full sweep (≥ 99.9%)
/// degrades to a complete ellipse — an arc whose endpoints coincide would
/// collapse to nothing.</para>
/// </summary>
public static class ProgressArcPresentation
{
    /// <summary>Outer diameter of the arc — the ProgressRing slot it replaces.</summary>
    public const double Size = 20;

    /// <summary>Stroke thickness of the arc.</summary>
    public const double Thickness = 2;

    /// <summary>
    /// The arc geometry for a 0..1 completion fraction: an arc from 12
    /// o'clock sweeping clockwise by <paramref name="fraction"/> of the full
    /// circle (a full ellipse once the fraction rounds to complete). Fractions
    /// ≤ 0 yield an empty geometry (nothing to draw yet). Never returns null —
    /// x:Bind feeds the result straight into <c>Path.Data</c>.
    /// </summary>
    public static Geometry Arc(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= 0)
            return new PathGeometry(); // nothing to draw yet

        var (endX, endY, isLargeArc, full) = Compute(fraction);

        var radius = (Size - Thickness) / 2;
        var center = Size / 2;

        if (full)
            return new EllipseGeometry
            {
                Center = new Point(center, center),
                RadiusX = radius,
                RadiusY = radius,
            };

        var figure = new PathFigure { StartPoint = new Point(center, center - radius) };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(endX, endY),
            Size = new Size(radius, radius),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Pure math behind <see cref="Arc"/> (kept separate for unit tests):
    /// the arc's endpoint and flags for a 0..1 fraction on the
    /// <see cref="Size"/>×<see cref="Size"/> ring. Out-of-range fractions are
    /// clamped; NaN reads as empty.
    /// </summary>
    internal static (double endX, double endY, bool isLargeArc, bool full) Compute(double fraction)
    {
        if (double.IsNaN(fraction)) fraction = 0;
        fraction = Math.Clamp(fraction, 0, 1);

        var radius = (Size - Thickness) / 2;
        var center = Size / 2;

        // Full circle: a dedicated ellipse (an arc can't close on itself).
        if (fraction >= 0.999)
            return (center, center - radius, true, true);

        var angle = fraction * 2 * Math.PI; // from 12 o'clock, clockwise
        return (
            center + radius * Math.Sin(angle),
            center - radius * Math.Cos(angle),
            fraction > 0.5,
            false);
    }
}
