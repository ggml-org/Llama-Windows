using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

public sealed class ModelPresetsTests
{
    [Fact]
    public void SavedPreferencesDoNotRecreateDeletedLocalModels()
    {
        var contexts = new Dictionary<string, int>
        {
            ["local/model-123"] = 8192,
            ["repo:Q4"] = 4096,
        };
        var profiles = new Dictionary<string, ModelPromptProcessingProfile>
        {
            ["local/model-123"] = new() { BatchSize = 512 },
        };
        var paths = new Dictionary<string, string>();

        var ini = ModelPresets.Render(contexts, profiles, localModelPaths: paths);
        Assert.DoesNotContain("local/model-123", ini);
        Assert.Contains("[repo:Q4]\nctx-size = 4096", ini);

        paths["local/model-123"] = @"C:\Models\Model.gguf";
        ini = ModelPresets.Render(contexts, profiles, localModelPaths: paths);
        Assert.Contains("[local/model-123]", ini);
        Assert.Contains("ctx-size = 8192", ini);
        Assert.Contains("batch-size = 512", ini);
    }

    [Fact]
    public void LocalFileAndPreferencesShareOnePresetSection()
    {
        var ini = ModelPresets.Render(
            new Dictionary<string, int> { ["local/model-123"] = 8192 }, null,
            localModelPaths: new Dictionary<string, string>
            {
                ["local/model-123"] = @"C:\Models\Publisher\Model Q4.gguf",
            });
        Assert.Equal(1, ini!.Split("[local/model-123]", StringSplitOptions.None).Length - 1);
        Assert.Contains("model = C:\\Models\\Publisher\\Model Q4.gguf", ini);
        Assert.Contains("ctx-size = 8192", ini);
    }

    [Fact]
    public void LocalFilePathCannotInjectPresetOptions()
    {
        var ini = ModelPresets.Render(null, null, localModelPaths:
            new Dictionary<string, string> { ["local/model"] = "file.gguf\n[other]" });
        Assert.Null(ini);
    }

    [Fact]
    public void ContextAndPromptOverridesShareOneModelSection()
    {
        var ini = ModelPresets.Render(
            new Dictionary<string, int> { ["repo:Q4"] = 32768 },
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["repo:Q4"] = new() { BatchSize = 1024, MicroBatchSize = 256, FlashAttention = "on" },
            });

        Assert.NotNull(ini);
        Assert.Equal(1, ini.Split("[repo:Q4]", StringSplitOptions.None).Length - 1);
        Assert.Contains("ctx-size = 32768", ini);
        Assert.Contains("batch-size = 1024", ini);
        Assert.Contains("ubatch-size = 256", ini);
        Assert.Contains("flash-attn = on", ini);
    }

    [Fact]
    public void AutomaticProfilesDoNotChangeExistingDefaults()
    {
        Assert.Null(ModelPresets.Render(null,
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["repo:Q4"] = new(),
            }));
    }

    [Fact]
    public void InvalidSectionNameCannotInjectAnotherPreset()
    {
        var ini = ModelPresets.Render(
            new Dictionary<string, int> { ["repo:Q4\n[other]"] = 8192 }, null);
        Assert.Null(ini);
    }

    [Fact]
    public void SeparateModelsKeepSeparateOverrides()
    {
        var ini = ModelPresets.Render(
            new Dictionary<string, int> { ["small:Q4"] = 8192 },
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["large:Q4"] = new() { FlashAttention = "off" },
            });
        Assert.NotNull(ini);
        Assert.Contains("[small:Q4]\nctx-size = 8192", ini);
        Assert.Contains("[large:Q4]\nflash-attn = off", ini);
    }

    [Fact]
    public void GlobalDefaultsAndModelOverridesUseSeparatePresetSections()
    {
        var ini = ModelPresets.Render(null,
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["model:Q4"] = new()
                {
                    BatchSize = 256,
                    CacheTypeK = "f16",
                    GpuDeviceName = "Radeon 8060S",
                    GpuLayers = "all",
                    Temperature = 0.4,
                    SplitMode = "layer",
                    TensorSplit = "3,1",
                },
            },
            new ModelPromptProcessingProfile { BatchSize = 1024, CacheTypeK = "q8_0", Threads = 8 },
            new Dictionary<string, string> { ["Radeon 8060S"] = "Vulkan0" }, "Vulkan1");

        Assert.NotNull(ini);
        Assert.Contains("[*]\nbatch-size = 1024\ndevice = Vulkan1\ncache-type-k = q8_0\nthreads = 8", ini);
        Assert.Contains("[model:Q4]\nbatch-size = 256\ndevice = Vulkan0\nn-gpu-layers = all\ncache-type-k = f16", ini);
        Assert.Contains("temp = 0.4", ini);
        Assert.Contains("split-mode = layer", ini);
        Assert.Contains("tensor-split = 3,1", ini);
    }

    [Fact]
    public void MissingRequestedModelGpuFallsBackToCpuInPreset()
    {
        var ini = ModelPresets.Render(null,
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["model:Q4"] = new() { GpuDeviceName = "Unplugged GPU", GpuLayers = "all" },
            }, deviceIds: new Dictionary<string, string>());

        Assert.Contains("device = none\nn-gpu-layers = 0", ini);
        Assert.DoesNotContain("n-gpu-layers = all", ini);
    }

    [Fact]
    public void SameNameGpuChoiceMapsToSelectedDevice()
    {
        var cards = new[]
        {
            new LlamaDevice { Id = "ROCm0", Name = "AMD Radeon", Kind = DeviceKind.Rocm },
            new LlamaDevice { Id = "ROCm1", Name = "AMD Radeon", Kind = DeviceKind.Rocm },
            new LlamaDevice { Id = "ROCm2", Name = "AMD Radeon", Kind = DeviceKind.Rocm },
        };
        var selected = GpuDeviceChoice.Key(cards[2], cards);
        var ini = ModelPresets.Render(null,
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["model:Q4"] = new() { GpuDeviceName = selected },
            }, deviceIds: cards.ToDictionary(card => GpuDeviceChoice.Key(card, cards),
                card => card.Id, StringComparer.OrdinalIgnoreCase));

        Assert.Contains("device = ROCm2", ini);
    }

    [Fact]
    public void InvalidExpertValueCannotEnterPreset()
    {
        var ini = ModelPresets.Render(null,
            new Dictionary<string, ModelPromptProcessingProfile>
            {
                ["model:Q4"] = new() { TensorSplit = "1,2\n[other]" },
            });
        Assert.Null(ini);
    }
}
