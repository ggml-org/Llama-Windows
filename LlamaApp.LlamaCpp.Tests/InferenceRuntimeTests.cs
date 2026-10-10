using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

public sealed class InferenceRuntimeTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("auto", 0)]
    [InlineData("rocm", 2)]
    [InlineData("vulkan", 2)]
    [InlineData("cpu", 3)]
    public void InstallerEnvironment_SelectsBackendWithCpuFallback(string? backend, int count)
    {
        var skips = InferenceRuntime.InstallerEnvironment(backend);
        Assert.Equal(count, skips.Count);
        if (backend == "rocm")
        {
            Assert.True(skips.ContainsKey("SKIP_CUDA"));
            Assert.True(skips.ContainsKey("SKIP_VULKAN"));
            Assert.False(skips.ContainsKey("SKIP_ROCM"));
        }
        if (backend == "vulkan")
        {
            Assert.True(skips.ContainsKey("SKIP_CUDA"));
            Assert.True(skips.ContainsKey("SKIP_ROCM"));
            Assert.False(skips.ContainsKey("SKIP_VULKAN"));
        }
    }

    [Fact]
    public void ReinstallOnlyWhenPreferenceChanges()
    {
        Assert.False(InferenceRuntime.RequiresManagedInstall("auto", null));
        Assert.False(InferenceRuntime.RequiresManagedInstall("ROCM", "rocm"));
        Assert.True(InferenceRuntime.RequiresManagedInstall("rocm", "auto"));
        Assert.True(InferenceRuntime.RequiresManagedInstall("auto", "vulkan"));
    }

    private static readonly DeviceProbe TwoRadeons = new(true,
    [
        new LlamaDevice { Id = "Vulkan0", Name = "AMD Radeon(TM) 8060S Graphics", Kind = DeviceKind.Vulkan },
        new LlamaDevice { Id = "Vulkan1", Name = "AMD Radeon RX 9060 XT", Kind = DeviceKind.Vulkan },
    ]);

    [Fact]
    public void AutomaticSelectionPreservesLlamaDefaults()
        => Assert.Null(InferenceRuntime.SelectDevice("auto", "", TwoRadeons).DeviceArgument);

    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics", "Vulkan0")]
    [InlineData("AMD Radeon RX 9060 XT", "Vulkan1")]
    public void SelectsEachRadeonByStableName(string name, string expectedId)
        => Assert.Equal(expectedId, InferenceRuntime.SelectDevice("vulkan", name, TwoRadeons).DeviceArgument);

    [Fact]
    public void MissingSelectedGpuFallsBackToCpu()
    {
        var selection = InferenceRuntime.SelectDevice("vulkan", "AMD Radeon RX 9060 XT", new DeviceProbe(true,
        [
            new LlamaDevice { Id = "Vulkan0", Name = "AMD Radeon(TM) 8060S Graphics", Kind = DeviceKind.Vulkan },
        ]));
        Assert.Equal("none", selection.DeviceArgument);
        Assert.Contains("9060 XT", selection.FallbackReason);
    }

    [Fact]
    public void WrongBackendFallsBackToCpu()
        => Assert.Equal("none", InferenceRuntime.SelectDevice("rocm", "", TwoRadeons).DeviceArgument);

    [Fact]
    public void CpuSelectionNeverUsesGpu()
        => Assert.Equal("none", InferenceRuntime.SelectDevice("cpu", "", TwoRadeons).DeviceArgument);

    [Fact]
    public void ThreeDifferentGpuVendorsRemainSelectable()
    {
        var devices = new DeviceProbe(true,
        [
            new LlamaDevice { Id = "Vulkan0", Name = "Intel Arc", Kind = DeviceKind.Vulkan },
            new LlamaDevice { Id = "Vulkan1", Name = "AMD Radeon", Kind = DeviceKind.Vulkan },
            new LlamaDevice { Id = "Vulkan2", Name = "NVIDIA GeForce", Kind = DeviceKind.Vulkan },
        ]);

        Assert.Equal("Vulkan0,Vulkan1,Vulkan2",
            InferenceRuntime.SelectDevice("vulkan", "", devices).DeviceArgument);
        Assert.Equal("Vulkan2",
            InferenceRuntime.SelectDevice("vulkan", "NVIDIA GeForce", devices).DeviceArgument);
    }

    [Fact]
    public void IdenticalNamesNeedBackendIndexAndDoNotSilentlySelectFirstCard()
    {
        var cards = new[]
        {
            new LlamaDevice { Id = "CUDA0", Name = "NVIDIA GeForce RTX", Kind = DeviceKind.Cuda },
            new LlamaDevice { Id = "CUDA1", Name = "NVIDIA GeForce RTX", Kind = DeviceKind.Cuda },
            new LlamaDevice { Id = "CUDA2", Name = "NVIDIA GeForce RTX", Kind = DeviceKind.Cuda },
        };
        var probe = new DeviceProbe(true, cards);
        var secondKey = GpuDeviceChoice.Key(cards[1], cards);

        Assert.NotEqual(GpuDeviceChoice.Key(cards[0], cards), secondKey);
        Assert.Equal("CUDA1", InferenceRuntime.SelectDevice("auto", secondKey, probe).DeviceArgument);
        Assert.Equal("none", InferenceRuntime.SelectDevice("auto", cards[1].Name, probe).DeviceArgument);
        Assert.Equal("none", InferenceRuntime.SelectDevice("auto", "gpu:CUDA9:NVIDIA GeForce RTX", probe).DeviceArgument);
    }

    [Fact]
    public void UniqueNameSurvivesDeviceIndexChange()
    {
        var saved = new LlamaDevice { Id = "ROCm1", Name = "AMD Radeon", Kind = DeviceKind.Rocm };
        Assert.Equal(saved.Name, GpuDeviceChoice.Key(saved, [saved]));
        var afterRestart = new DeviceProbe(true,
        [new LlamaDevice { Id = "ROCm0", Name = saved.Name, Kind = DeviceKind.Rocm }]);
        Assert.Equal("ROCm0", InferenceRuntime.SelectDevice("rocm", saved.Name, afterRestart).DeviceArgument);
    }

    [Fact]
    public void PickerShowsFreeAndTotalMemory()
    {
        var card = new LlamaDevice
        {
            Id = "Vulkan2", Name = "Intel Arc", Kind = DeviceKind.Vulkan,
            FreeBytes = 8_000_000_000, TotalBytes = 12_000_000_000,
        };
        Assert.Equal("Intel Arc (Vulkan2) · 8 GB free / 12 GB total",
            GpuDeviceChoice.Label(card));
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData("all", "all")]
    [InlineData("42", "42")]
    [InlineData("-1", null)]
    [InlineData("1001", null)]
    public void GpuLayerValueIsValidated(string setting, string? expected)
        => Assert.Equal(expected, InferenceRuntime.GpuLayersArgument(setting));
}
