using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for parsing the <c>llama --list-devices</c> output into
/// <see cref="LlamaDevice"/>s — covering both the "(none)" shape (no
/// accelerator, CPU/RAM fallback) and populated device sections, with the
/// backend-init noise the CLI prints around the section.
/// </summary>
public class DeviceQueryTests
{
    [Fact]
    public void Parse_None_Reports_No_Devices()
    {
        var output = """
            Available devices:
              (none)
            """;

        var devices = DeviceQuery.Parse(output);

        Assert.Empty(devices);
    }

    [Fact]
    public void Parse_Single_Cuda_Device_With_Init_Noise()
    {
        // Real-world capture: backend init lines precede the section.
        var output = """
            gml_cuda_init: found 1 CUDA devices (Total VRAM: 15944 MiB):
              Device 0: NVIDIA GeForce RTX 4060 Ti, compute capability 8.9, VMM: yes, VRAM: 15944 MiB
            load_backend: loaded CUDA backend from /app/libggml-cuda.so
            load_backend: loaded CPU backend from /app/libggml-cpu-haswell.so
            Available devices:
              CUDA0: NVIDIA GeForce RTX 4060 Ti (15944 MiB, 14143 MiB free)
            """;

        var devices = DeviceQuery.Parse(output);

        var device = Assert.Single(devices);
        Assert.Equal("CUDA0", device.Id);
        Assert.Equal("NVIDIA GeForce RTX 4060 Ti", device.Name);
        Assert.Equal(DeviceKind.Cuda, device.Kind);
        Assert.Equal(15944UL << 20, device.TotalBytes);
        Assert.Equal(14143UL << 20, device.FreeBytes);
    }

    [Fact]
    public void Parse_Multiple_Devices_Of_Different_Backends()
    {
        var output = """
            Available devices:
              CUDA0: NVIDIA GeForce RTX 4090 (24564 MiB, 22000 MiB free)
              Vulkan0: AMD Radeon RX 7900 XTX (24560 MiB, 20000 MiB free)
              Metal0: Apple M3 Ultra (192 GiB, 100 GiB free)
            """;

        var devices = DeviceQuery.Parse(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(DeviceKind.Cuda, devices[0].Kind);
        Assert.Equal(DeviceKind.Vulkan, devices[1].Kind);
        Assert.Equal(DeviceKind.Metal, devices[2].Kind);
        Assert.Equal(192UL << 30, devices[2].TotalBytes);
        Assert.Equal(100UL << 30, devices[2].FreeBytes);
    }

    [Fact]
    public void Parse_Section_Ends_At_First_Non_Indented_Line()
    {
        var output = """
            Available devices:
              CUDA0: NVIDIA GeForce RTX 4060 Ti (15944 MiB, 14143 MiB free)
            some trailing noise
              CUDA1: not really part of the section (1 MiB, 1 MiB free)
            """;

        var devices = DeviceQuery.Parse(output);

        Assert.Single(devices);
    }

    [Fact]
    public void Parse_Without_Header_Returns_Nothing()
    {
        var output = """
            load_backend: loaded CPU backend from /app/libggml-cpu-haswell.so
              CUDA0: NVIDIA GeForce RTX 4060 Ti (15944 MiB, 14143 MiB free)
            """;

        Assert.Empty(DeviceQuery.Parse(output));
    }

    [Fact]
    public void Parse_Garbage_Entry_Is_Skipped_Not_Fatal()
    {
        var output = """
            Available devices:
              totally unparsable line
              CUDA0: NVIDIA GeForce RTX 4060 Ti (15944 MiB, 14143 MiB free)
            """;

        var devices = DeviceQuery.Parse(output);

        Assert.Single(devices);
    }

    [Theory]
    [InlineData("15944", "MiB", 15944UL << 20)]
    [InlineData("1.5", "GiB", (ulong)(1.5 * (1UL << 30)))]
    [InlineData("512", "KiB", 512UL << 10)]
    [InlineData("2", "GB", 2_000_000_000UL)]
    [InlineData("1024", "B", 1024UL)]
    public void TryParseSize_Converts_Units(string value, string unit, ulong expected)
    {
        Assert.True(DeviceQuery.TryParseSize(value, unit, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("", "MiB")]
    [InlineData("abc", "MiB")]
    [InlineData("-1", "MiB")]
    [InlineData("10", "parsecs")]
    public void TryParseSize_Rejects_Malformed_Input(string value, string unit)
    {
        Assert.False(DeviceQuery.TryParseSize(value, unit, out _));
    }

    [Theory]
    [InlineData("CUDA0", DeviceKind.Cuda)]
    [InlineData("Vulkan1", DeviceKind.Vulkan)]
    [InlineData("Metal0", DeviceKind.Metal)]
    [InlineData("CPU", DeviceKind.Cpu)]
    [InlineData("SYCL0", DeviceKind.Unknown)]
    public void KindFromId_Maps_Prefixes(string id, DeviceKind expected)
    {
        Assert.Equal(expected, DeviceQuery.KindFromId(id));
    }
}
