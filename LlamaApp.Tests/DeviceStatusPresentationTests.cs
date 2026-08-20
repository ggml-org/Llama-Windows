using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceStatusPresentation"/> — the pure mapping
/// from probed accelerator devices to the footer's GPU indicator. The
/// visibility rule matters most: the glyph shows only when a device probe
/// actually found an accelerator, so a CPU-only machine (or a probe that ran
/// before the llama binary existed) sees no indicator at all.
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
    public void No_Devices_Hides_The_Indicator()
    {
        var d = DeviceStatusPresentation.Describe([]);

        Assert.False(d.Visible);
        Assert.Equal("", d.ToolTip);
    }

    [Fact]
    public void Single_Device_Shows_With_Name_And_Free_Memory()
    {
        var d = DeviceStatusPresentation.Describe(
            [Gpu("NVIDIA GeForce RTX 4060 Ti", 14_143UL << 20)]);

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
        var d = DeviceStatusPresentation.Describe(
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
}
