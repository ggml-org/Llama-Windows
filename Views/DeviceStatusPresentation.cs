using LlamaApp.Llama;

namespace LlamaApp.Views
{
    /// <summary>
    /// Pure mapping from the probed accelerator devices
    /// (<see cref="LlamaManager.ListDevicesAsync"/>) to the footer's GPU
    /// indicator — whether it shows and what its tooltip says. Kept separate
    /// from <see cref="MainWindow"/> so the rules are unit-testable,
    /// mirroring <see cref="ServerStatusPresentation"/>.
    /// </summary>
    public static class DeviceStatusPresentation
    {
        /// <summary>The rendered footer indicator: visible when at least one
        /// accelerator device was probed, plus the tooltip describing them.</summary>
        public readonly record struct Description(bool Visible, string ToolTip);

        /// <summary>
        /// Maps the probed <paramref name="devices"/> to the footer
        /// rendering. No devices (CPU-only machine, CPU-only llama build, or
        /// a failed/early probe) hides the indicator entirely — silence, not
        /// a grayed-out hint. With devices, the tooltip names them with
        /// their free memory: <c>NVIDIA GeForce RTX 4060 Ti (14.1 GB free)</c>.
        /// </summary>
        public static Description Describe(IReadOnlyList<LlamaDevice> devices)
        {
            if (devices.Count == 0)
                return new(false, "");

            return new(true, devices.Count == 1
                ? $"GPU acceleration available: {DescribeDeviceList(devices)}"
                : $"GPU acceleration available on {devices.Count} devices: {DescribeDeviceList(devices)}");
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
