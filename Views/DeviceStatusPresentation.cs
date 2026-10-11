using LlamaApp.Llama;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from the device probe
    /// (<see cref="LlamaManager.ProbeDevicesAsync"/>) to the footer's device
    /// indicator — which glyph shows (GPU card / CPU chip / none) and what
    /// its tooltip says. Kept separate from <see cref="MainWindow"/> so the
    /// rules are unit-testable, mirroring <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class DeviceStatusPresentation
    {
        /// <summary>Which glyph the footer shows for the device probe.</summary>
        public enum IndicatorKind
        {
            /// <summary>No indicator — the probe failed or never ran (the
            /// footer stays silent rather than guessing).</summary>
            None,
            /// <summary>The GPU card glyph — at least one accelerator device
            /// (CUDA / Vulkan) was probed.</summary>
            Gpu,
            /// <summary>The CPU chip glyph — the probe succeeded but found no
            /// accelerator (CPU-only machine or CPU-only llama build).</summary>
            Cpu,
        }

        /// <summary>The rendered footer indicator: which glyph is visible
        /// plus the tooltip describing the devices.</summary>
        public readonly record struct Description(IndicatorKind Kind, string ToolTip)
        {
            public bool Visible => Kind != IndicatorKind.None;
        }

        /// <summary>
        /// Maps the probe result to the footer rendering:
        /// <list type="bullet">
        /// <item>accelerator devices found → the GPU card glyph; the tooltip
        /// names them with their free memory: <c>NVIDIA GeForce RTX 4060 Ti
        /// (14.1 GB free)</c>;</item>
        /// <item>probe succeeded with no accelerators (or only CPU devices)
        /// → the CPU chip glyph with a "no accelerator" note;</item>
        /// <item>probe failed / binary not resolved → nothing at all —
        /// silence, not a grayed-out hint.</item>
        /// </list>
        /// </summary>
        public static Description Describe(bool probeSucceeded, IReadOnlyList<LlamaDevice> devices,
            string? activeDeviceId = null, string? fallbackReason = null)
        {
            var accelerators = devices.Where(d => d.Kind != DeviceKind.Cpu).ToList();

            if (activeDeviceId == "none")
                return new Description(IndicatorKind.Cpu,
                    fallbackReason ?? "CPU inference selected");

            if (accelerators.Count > 0)
            {
                if (activeDeviceId is not null)
                    return new Description(IndicatorKind.Gpu,
                        $"Inference {(accelerators.Count == 1 ? "GPU" : "GPUs")}: {DescribeDeviceList(accelerators)}");
                return new Description(IndicatorKind.Gpu, accelerators.Count == 1
                    ? $"GPU acceleration available: {DescribeDeviceList(accelerators)}"
                    : $"GPU acceleration available on {accelerators.Count} devices: {DescribeDeviceList(accelerators)}");
            }

            if (probeSucceeded)
                return new Description(IndicatorKind.Cpu,
                    "No accelerator detected — models run on the CPU");

            return new Description(IndicatorKind.None, "");
        }

        /// <summary>
        /// The device summary used in tooltips and the not-enough-memory
        /// wording: each device's name plus its free memory, joined for
        /// multi-GPU machines — e.g. <c>NVIDIA GeForce RTX 4060 Ti
        /// (14.1 GB free)</c>. Free (not total) memory is quoted because
        /// that is the number the fit checks actually budget against.
        /// </summary>
        public static string DescribeDeviceList(IReadOnlyList<LlamaDevice> devices)
            => string.Join(", ", devices.Select(d =>
                $"{d.Name} ({MemoryFit.FormatBytes(d.FreeBytes)} free)"));
    }
}
