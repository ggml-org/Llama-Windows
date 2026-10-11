using System;
using System.Text.RegularExpressions;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from a raw quantization label ("Q4_K_M") to a friendly,
    /// provider-neutral quality name ("Balanced") for the family details
    /// variant picker — so choosing a model version doesn't require knowing
    /// the quant alphabet up front. The raw identifier still rides along in
    /// the variant row as secondary metadata. Kept separate from the view
    /// models so the rules stay unit-testable, mirroring
    /// <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class QuantizationPresentation
    {
        private static readonly Regex FileQuant = new(
            @"(?:^|[._\s()-])(?<quant>IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[2-8]_K(?:_[SML])?|Q[4-8]_[01](?:_[48]_[48])?|TQ[12]_0|MXFP4|NVFP4|BF16|F16|F32)(?=$|[._\s()-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        public static string? FromFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var match = FileQuant.Match(Path.GetFileNameWithoutExtension(path));
            return match.Success ? match.Groups["quant"].Value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// The friendly quality tier for a quant label. Unknown labels are
        /// returned unchanged so a new quant never renders as a blank row.
        /// </summary>
        public static string FriendlyName(string? quant)
        {
            if (string.IsNullOrWhiteSpace(quant)) return quant ?? "";
            var q = quant.Trim();

            // I-quants: smallest, most aggressive compression.
            if (q.StartsWith("IQ", StringComparison.OrdinalIgnoreCase))
                return "Compact";

            // MXFP4 (OpenAI's ~4.5-bit format) sits with the Q4s.
            if (q.StartsWith("mxfp", StringComparison.OrdinalIgnoreCase))
                return "Balanced";

            // Legacy Q-quants: the first digit is the weight bit-width class.
            if (q.Length >= 2 && (q[0] == 'Q' || q[0] == 'q') && char.IsDigit(q[1]))
            {
                switch (q[1])
                {
                    case '2':
                    case '3':
                        return "Compact";
                    case '4':
                        return "Balanced";
                    case '5':
                    case '6':
                    case '8':
                        return "Higher quality";
                }
            }

            // Full-precision floats.
            if (q.StartsWith("F16", StringComparison.OrdinalIgnoreCase) ||
                q.StartsWith("BF16", StringComparison.OrdinalIgnoreCase) ||
                q.StartsWith("F32", StringComparison.OrdinalIgnoreCase))
                return "Higher quality";

            return quant;
        }
    }
}
