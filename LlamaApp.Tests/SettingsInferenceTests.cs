using System.Text.Json;
using Xunit;

namespace LlamaApp.Tests;

public sealed class SettingsInferenceTests
{
    [Fact]
    public void OlderSettingsKeepAutomaticInference()
    {
        var settings = JsonSerializer.Deserialize<Settings>("{}");
        Assert.NotNull(settings);
        Assert.Equal("auto", settings.RuntimeBackend);
        Assert.Equal("", settings.GpuDeviceName);
        Assert.Equal("auto", settings.GpuLayers);
        Assert.Equal(0, settings.BatchSize);
        Assert.Equal(0, settings.MicroBatchSize);
        Assert.Equal("auto", settings.FlashAttention);
    }

    [Fact]
    public void SelectedRuntimeGpuAndLayersRoundTrip()
    {
        var settings = new Settings
        {
            RuntimeBackend = "vulkan",
            GpuDeviceName = "AMD Radeon RX 9060 XT",
            GpuLayers = "42",
            BatchSize = 1024,
            MicroBatchSize = 256,
            FlashAttention = "on",
            ModelPromptProfiles = new()
            {
                ["repo:Q4"] = new LlamaApp.Llama.ModelPromptProcessingProfile
                {
                    BatchSize = 512,
                    MicroBatchSize = 128,
                    FlashAttention = "off",
                },
            },
        };
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings));
        Assert.NotNull(loaded);
        Assert.Equal(settings.RuntimeBackend, loaded.RuntimeBackend);
        Assert.Equal(settings.GpuDeviceName, loaded.GpuDeviceName);
        Assert.Equal(settings.GpuLayers, loaded.GpuLayers);
        Assert.Equal(settings.BatchSize, loaded.BatchSize);
        Assert.Equal(settings.MicroBatchSize, loaded.MicroBatchSize);
        Assert.Equal(settings.FlashAttention, loaded.FlashAttention);
        Assert.Equal(512, loaded.ModelPromptProfiles["repo:Q4"].BatchSize);
        Assert.Equal(128, loaded.ModelPromptProfiles["repo:Q4"].MicroBatchSize);
        Assert.Equal("off", loaded.ModelPromptProfiles["repo:Q4"].FlashAttention);
    }
}
