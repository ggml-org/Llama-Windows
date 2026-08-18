using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for the fit decision (<see cref="MemoryFit.Check"/>) — GPU
/// budget (summed free VRAM), the CPU/RAM fallback when no devices exist,
/// the partial-offload path when VRAM alone is too small, and the fail-open
/// rules for unknown estimates and failed probes.
/// </summary>
public class MemoryFitTests
{
    private static LlamaDevice Device(string id, string name, ulong freeMiB) => new()
    {
        Id = id,
        Name = name,
        TotalBytes = freeMiB << 20,
        FreeBytes = freeMiB << 20,
        Kind = DeviceQuery.KindFromId(id),
    };

    private static MemoryEstimate Estimate(ulong totalBytes) => new()
    {
        WeightBytes = totalBytes,
        KvCacheBytes = 0,
        OverheadBytes = 0,
        ContextSize = 4096,
    };

    [Fact]
    public void Fits_In_Single_Gpu_Free_Vram()
    {
        // The user's sample machine: 14143 MiB free on the RTX 4060 Ti.
        var devices = new[] { Device("CUDA0", "NVIDIA GeForce RTX 4060 Ti", 14_143) };
        var estimate = Estimate(13UL << 30); // 13 GB — a gpt-oss-20b-class model

        var result = MemoryFit.Check(estimate, devices, 16UL << 30);

        Assert.True(result.Fits);
        Assert.Equal(FitTarget.Gpu, result.Target);
        Assert.Equal(14_143UL << 20, result.AvailableBytes);
    }

    [Fact]
    public void Gpu_Budget_Is_The_Sum_Of_All_Devices()
    {
        // Doesn't fit either GPU alone, but fits their combined free VRAM
        // (llama.cpp splits layers across devices).
        var devices = new[]
        {
            Device("CUDA0", "GPU A", 8_000),
            Device("CUDA1", "GPU B", 8_000),
        };

        var result = MemoryFit.Check(Estimate(12UL << 30), devices, null);

        Assert.True(result.Fits);
        Assert.Equal(FitTarget.Gpu, result.Target);
        Assert.Equal(16_000UL << 20, result.AvailableBytes);
    }

    [Fact]
    public void Falls_Back_To_Cpu_When_Vram_Too_Small_But_Ram_Fits()
    {
        var devices = new[] { Device("CUDA0", "NVIDIA GeForce RTX 4060 Ti", 14_143) };
        // 30 GB model, 64 GB of RAM with 48 GB available → 36 GB usable at
        // the 0.75 budget fraction.
        var result = MemoryFit.Check(Estimate(30UL << 30), devices, 48UL << 30);

        Assert.True(result.Fits);
        Assert.Equal(FitTarget.Cpu, result.Target);
    }

    [Fact]
    public void No_Devices_Uses_Cpu_Ram_Budget()
    {
        // No accelerators, 20 GB available RAM → 15 GB usable (0.75
        // fraction); a 14 GB model fits, a 16 GB one doesn't.
        var fits = MemoryFit.Check(Estimate(14UL << 30), [], 20UL << 30);
        var noFit = MemoryFit.Check(Estimate(16UL << 30), [], 20UL << 30);

        Assert.True(fits.Fits);
        Assert.Equal(FitTarget.Cpu, fits.Target);
        Assert.False(noFit.Fits);
        Assert.Equal(FitTarget.None, noFit.Target);
    }

    [Fact]
    public void Does_Not_Fit_When_Nothing_Has_Room()
    {
        var devices = new[] { Device("CUDA0", "NVIDIA GeForce RTX 4060 Ti", 14_143) };
        // 120B-class model: too big for the VRAM and for 32 GB of RAM.
        var result = MemoryFit.Check(Estimate(64UL << 30), devices, 10UL << 30);

        Assert.False(result.Fits);
        Assert.Equal(FitTarget.None, result.Target);
        Assert.Equal(64UL << 30, result.RequiredBytes);
        // Messaging gets the larger budget: the 14143 MiB of free VRAM.
        Assert.Equal(14_143UL << 20, result.AvailableBytes);
    }

    [Fact]
    public void Unknown_Estimate_Fails_Open()
    {
        var result = MemoryFit.Check(new MemoryEstimate(), [], 1UL << 30);

        Assert.True(result.Fits); // no numbers → never block
        Assert.Equal(0UL, result.RequiredBytes);
    }

    [Fact]
    public void Failed_Ram_Probe_With_No_Devices_Fails_Open()
    {
        // availableSystemBytes = null → the RAM probe failed; without devices
        // there is no budget at all, and an unknown budget never blocks.
        var result = MemoryFit.Check(Estimate(100UL << 30), [], null);

        Assert.True(result.Fits);
    }

    [Fact]
    public void Failed_Ram_Probe_With_Devices_Still_Checks_Vram()
    {
        var devices = new[] { Device("CUDA0", "GPU", 14_143) };

        var fits = MemoryFit.Check(Estimate(10UL << 30), devices, null);
        var noFit = MemoryFit.Check(Estimate(20UL << 30), devices, null);

        Assert.True(fits.Fits);
        Assert.Equal(FitTarget.Gpu, fits.Target);
        Assert.False(noFit.Fits); // no VRAM, no RAM numbers → doesn't fit
    }

    [Fact]
    public void Devices_Are_Surfaced_For_Messaging()
    {
        var devices = new[] { Device("CUDA0", "NVIDIA GeForce RTX 4060 Ti", 14_143) };

        var result = MemoryFit.Check(Estimate(100UL << 30), devices, null);

        Assert.Equal(devices, result.Devices);
        Assert.Contains("RTX 4060 Ti", result.Details);
    }

    [Theory]
    [InlineData(1UL << 20, "1 MB")]
    [InlineData(2_500_000_000UL, "2.5 GB")]
    [InlineData(12_109_566_560UL, "12.1 GB")]
    [InlineData(2UL << 40, "2.2 TB")]
    public void FormatBytes_Uses_Decimal_Units(ulong bytes, string expected)
    {
        Assert.Equal(expected, MemoryFit.FormatBytes(bytes));
    }
}
