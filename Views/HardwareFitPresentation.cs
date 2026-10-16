using LlamaApp.Llama;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from a <see cref="HardwareFitLevel"/> verdict to its UI
    /// skin: the green/orange/red brush a model variation is tinted with —
    /// the color alone shows the level on screen — plus the short name
    /// ("Fits" / "Might fit" / "Won't fit") that spells it out for
    /// accessible names and narration. Kept separate from the view models
    /// so the rules stay unit-testable, mirroring
    /// <see cref="QuantizationPresentation"/>.
    /// </summary>
    public static class HardwareFitPresentation
    {
        /// <summary>Green — comfortably fits this machine.</summary>
        public static readonly Color FitColor = Color.FromArgb(0xFF, 0x2B, 0x9A, 0x46);

        /// <summary>Orange — tight, slightly over, or unknown.</summary>
        public static readonly Color MightColor = Color.FromArgb(0xFF, 0xD0, 0x7C, 0x10);

        /// <summary>Red — won't fit this machine.</summary>
        public static readonly Color NoWayColor = Color.FromArgb(0xFF, 0xD9, 0x3B, 0x30);

        /// <summary>
        /// The short name for a level. The visible UI trusts the color —
        /// this name spells the level out where color can't reach:
        /// accessible names and narration. Plain wording on purpose; the
        /// quant row already carries the technical detail.
        /// </summary>
        public static string Label(HardwareFitLevel level) => level switch
        {
            HardwareFitLevel.Fit => "Fits",
            HardwareFitLevel.Might => "Might fit",
            HardwareFitLevel.NoWay => "Won't fit",
            _ => "",
        };

        /// <summary>
        /// The row tint for a level. Mid-saturation values that read as
        /// green/orange/red on both the light and the dark Mica surface.
        /// </summary>
        public static Color ColorFor(HardwareFitLevel level) => level switch
        {
            HardwareFitLevel.Fit => FitColor,
            HardwareFitLevel.Might => MightColor,
            HardwareFitLevel.NoWay => NoWayColor,
            _ => MightColor,
        };

        /// <summary>The row tint brush for a level.</summary>
        public static Brush BrushFor(HardwareFitLevel level) =>
            new SolidColorBrush(ColorFor(level));

        /// <summary>
        /// The text brush to fall back to when no verdict has landed yet
        /// (rows render before the fit probe answers): the theme's primary
        /// text color so an unverdicted row looks like any other row.
        /// Resolved per call — the app's theme dictionaries swap their
        /// brushes when the OS theme flips. Outside a XAML application
        /// (unit tests) this is just a placeholder; brush construction is
        /// never exercised by the tests.
        /// </summary>
        public static Brush DefaultTextBrush
        {
            get
            {
                var app = Application.Current;
                if (app is null)
                    return new SolidColorBrush(Colors.White);
                if (app.Resources.TryGetValue("TextFillColorPrimaryBrush", out var resource)
                    && resource is Brush brush)
                    return brush;
                return new SolidColorBrush(
                    app.RequestedTheme == ApplicationTheme.Dark ? Colors.White : Colors.Black);
            }
        }
    }
}
