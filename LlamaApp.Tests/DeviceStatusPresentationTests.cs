using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceStatusPresentation"/> — the pure mapping
/// from the device probe to the footer's device indicator: the GPU card
/// glyph when accelerators were found, the CPU chip glyph when a probe
/// succeeded without any, and nothing at all when the probe failed (the
/// footer must never guess).
/// </summary>
public class DeviceStatusPresentationTests
{
    private static LlamaDevice Gpu(string name, ulong freeBytes, string id = "CUDA0") => new()
    {
        Id = id,
        Name = name,
        TotalBytes = freeBytes * 2,
        FreeBytes = freeBytes,
        Kind = DeviceKind.Cuda,
    };

    [Fact]
    public void Failed_Probe_Hides_The_Indicator()
    {
        // No devices AND the probe never succeeded (binary missing, timeout,
        // unreadable output) — the footer stays silent rather than claiming
        // "CPU only" on a machine that may well have a GPU.
        var d = DeviceStatusPresentation.Describe(probeSucceeded: false, []);

        Assert.Equal(DeviceStatusPresentation.IndicatorKind.None, d.Kind);
        Assert.False(d.Visible);
        Assert.Equal("", d.ToolTip);
    }

    [Fact]
    public void Successful_Empty_Probe_Shows_The_Cpu_Indicator()
    {
        // "(none)" from the CLI = genuinely no accelerator → the dimmed CPU
        // chip, honestly labeled.
        var d = DeviceStatusPresentation.Describe(probeSucceeded: true, []);

        Assert.Equal(DeviceStatusPresentation.IndicatorKind.Cpu, d.Kind);
        Assert.True(d.Visible);
        Assert.Contains("No accelerator detected", d.ToolTip);
    }

    [Fact]
    public void Cpu_Only_Devices_Count_As_No_Accelerator()
    {
        // Some builds list the CPU itself as a device — that's still a
        // CPU-only machine as far as acceleration goes.
        var cpu = new LlamaDevice
        {
            Id = "CPU0",
            Name = "AMD Ryzen 9 7950X",
            TotalBytes = 64UL << 30,
            FreeBytes = 48UL << 30,
            Kind = DeviceKind.Cpu,
        };

        var d = DeviceStatusPresentation.Describe(probeSucceeded: true, [cpu]);

        Assert.Equal(DeviceStatusPresentation.IndicatorKind.Cpu, d.Kind);
        Assert.Contains("No accelerator detected", d.ToolTip);
    }

    [Fact]
    public void Single_Device_Shows_With_Name_And_Free_Memory()
    {
        var d = DeviceStatusPresentation.Describe(probeSucceeded: true,
            [Gpu("NVIDIA GeForce RTX 4060 Ti", 14_143UL << 20)]);

        Assert.Equal(DeviceStatusPresentation.IndicatorKind.Gpu, d.Kind);

        Assert.True(d.Visible);
        Assert.Contains("GPU acceleration available", d.ToolTip);
        Assert.Contains("NVIDIA GeForce RTX 4060 Ti", d.ToolTip);
        Assert.Contains("free", d.ToolTip);
        // No "N devices" count for a single-GPU machine.
        Assert.DoesNotContain("devices", d.ToolTip);
    }

    [Fact]
    public void Multiple_Devices_List_Each_And_Count_Them()
    {
        var d = DeviceStatusPresentation.Describe(probeSucceeded: true,
        [
            Gpu("NVIDIA GeForce RTX 4060 Ti", 14UL << 30, "CUDA0"),
            Gpu("AMD Radeon RX 7900 XTX", 20UL << 30, "Vulkan0"),
        ]);

        Assert.True(d.Visible);
        Assert.Contains("2 devices", d.ToolTip);
        Assert.Contains("NVIDIA GeForce RTX 4060 Ti", d.ToolTip);
        Assert.Contains("AMD Radeon RX 7900 XTX", d.ToolTip);
    }

    [Fact]
    public void Free_Memory_Uses_The_Same_Format_As_Fit_Messaging()
    {
        const ulong free = 14_143UL << 20;
        var list = DeviceStatusPresentation.DescribeDeviceList([Gpu("GPU", free)]);

        Assert.Equal($"GPU ({MemoryFit.FormatBytes(free)} free)", list);
    }

    [Fact]
    public void DescribeDeviceList_Empty_For_No_Devices()
    {
        Assert.Equal("", DeviceStatusPresentation.DescribeDeviceList([]));
    }

    [Fact]
    public void SelectedGpuTooltipNamesOnlyTheChosenRadeon()
    {
        var selected = Gpu("AMD Radeon RX 9060 XT", 15UL << 30, "Vulkan1");
        var d = DeviceStatusPresentation.Describe(true, [selected], "Vulkan1");
        Assert.Equal(DeviceStatusPresentation.IndicatorKind.Gpu, d.Kind);
        Assert.Contains("Inference GPU", d.ToolTip);
        Assert.Contains("RX 9060 XT", d.ToolTip);
    }

    [Fact]
    public void CpuFallbackTooltipExplainsMissingGpu()
    {
        var d = DeviceStatusPresentation.Describe(true, [], "none",
            "AMD Radeon RX 9060 XT is unavailable; using the CPU.");
        Assert.Equal(DeviceStatusPresentation.IndicatorKind.Cpu, d.Kind);
        Assert.Contains("9060 XT is unavailable", d.ToolTip);
    }
}
