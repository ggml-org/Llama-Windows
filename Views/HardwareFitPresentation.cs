using LlamaApp.Llama;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from a <see cref="HardwareFitLevel"/> verdict to its UI
    /// skin: the badge the Hugging Face Hub shows for hardware compatibility
    /// — a colored tile with a white glyph (Assets/Fit/&lt;badge&gt;.svg, the
    /// hub's "fits" badge verbatim plus siblings drawn in the same style) —
    /// plus the short name ("Fits" / "Might fit" / "Won't fit") that spells
    /// the level out where color can't reach (accessible names). The visible
    /// UI shows the badge only. The palette is the hub's own: its CSS
    /// defines these badges with the Tailwind v4 tokens green-600,
    /// yellow-500 and red-600 (oklch), converted to sRGB here and in the
    /// SVG fills so the badges render identically in both places. Kept
    /// separate from the view models so the rules stay unit-testable,
    /// mirroring <see cref="QuantizationPresentation"/>.
    /// </summary>
    public static class HardwareFitPresentation
    {
        /// <summary>Green — comfortably fits this machine (green-600, the
        /// hub badge's fill).</summary>
        public static readonly Color FitColor = Color.FromArgb(0xFF, 0x00, 0x99, 0x66);

        /// <summary>Yellow — tight, slightly over, or unknown (yellow-500).</summary>
        public static readonly Color MightColor = Color.FromArgb(0xFF, 0xFE, 0x9A, 0x00);

        /// <summary>Red — won't fit this machine (red-600).</summary>
        public static readonly Color NoWayColor = Color.FromArgb(0xFF, 0xE7, 0x00, 0x0B);

        /// <summary>
        /// The short name for a level. The visible UI trusts the badge —
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
        /// The level's color — the palette the badge SVGs are filled with.
        /// </summary>
        public static Color ColorFor(HardwareFitLevel level) => level switch
        {
            HardwareFitLevel.Fit => FitColor,
            HardwareFitLevel.Might => MightColor,
            HardwareFitLevel.NoWay => NoWayColor,
            _ => MightColor,
        };

        /// <summary>
        /// The hub-style badge for a level (Assets/Fit/&lt;badge&gt;.svg):
        /// the green/yellow/red tile with its white check, question mark or
        /// cross.
        /// Created on demand and cached per level (XAML objects — never
        /// exercised by the unit tests, same as the logo pipeline's
        /// <c>SvgImageSource</c>s). Bind it to an Image; a missing verdict
        /// is the caller's null and shows nothing.
        /// </summary>
        public static ImageSource BadgeSource(HardwareFitLevel level)
        {
            lock (BadgeCacheLock)
            {
                if (BadgeCache.TryGetValue(level, out var cached))
                    return cached;

                var source = new SvgImageSource(new Uri($"ms-appx:///Assets/Fit/{BadgeFile(level)}.svg"))
                {
                    RasterizePixelWidth = 32,
                };
                BadgeCache[level] = source;
                return source;
            }
        }

        private static string BadgeFile(HardwareFitLevel level) => level switch
        {
            HardwareFitLevel.Fit => "fit",
            HardwareFitLevel.Might => "might-fit",
            _ => "no-way",
        };

        private static readonly Dictionary<HardwareFitLevel, ImageSource> BadgeCache = new();
        private static readonly object BadgeCacheLock = new();
    }
}
