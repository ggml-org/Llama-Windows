using System.Text;

namespace LlamaApp.Llama;

/// <summary>Optional prompt-processing overrides for one server model id.</summary>
public sealed record ModelPromptProcessingProfile
{
    public int BatchSize { get; init; }
    public int MicroBatchSize { get; init; }
    public string FlashAttention { get; init; } = "auto";

    /// <summary>Empty inherits the server choice; "none" selects CPU; identical cards also store a backend id.</summary>
    public string GpuDeviceName { get; init; } = "";
    /// <summary>Empty inherits; auto, all, or a layer count overrides it.</summary>
    public string GpuLayers { get; init; } = "";
    /// <summary>Empty inherits the server's KV type.</summary>
    public string CacheTypeK { get; init; } = "";
    public string CacheTypeV { get; init; } = "";
    public int Threads { get; init; }
    public int ThreadsBatch { get; init; }
    public int Parallel { get; init; }
    public double? Temperature { get; init; }
    public int? TopK { get; init; }
    public double? TopP { get; init; }
    public double? RepeatPenalty { get; init; }
    public string SplitMode { get; init; } = "";
    public string TensorSplit { get; init; } = "";

    public bool IsAutomatic => !InferenceTuning.HasOverrides(BatchSize, MicroBatchSize, FlashAttention)
        && string.IsNullOrEmpty(GpuDeviceName) && string.IsNullOrEmpty(GpuLayers)
        && string.IsNullOrEmpty(CacheTypeK) && string.IsNullOrEmpty(CacheTypeV)
        && Threads == 0 && ThreadsBatch == 0 && Parallel == 0
        && Temperature is null && TopK is null && TopP is null && RepeatPenalty is null
        && string.IsNullOrEmpty(SplitMode) && string.IsNullOrEmpty(TensorSplit);
}

/// <summary>Renders the router's model-specific INI from validated preferences.</summary>
internal static class ModelPresets
{
    internal static string? Render(
        IReadOnlyDictionary<string, int>? contexts,
        IReadOnlyDictionary<string, ModelPromptProcessingProfile>? profiles,
        ModelPromptProcessingProfile? global = null,
        IReadOnlyDictionary<string, string>? deviceIds = null,
        string? globalDeviceId = null,
        IReadOnlyDictionary<string, string>? localModelPaths = null)
    {
        var ids = (contexts?.Keys ?? Enumerable.Empty<string>())
            .Concat(profiles?.Keys ?? Enumerable.Empty<string>())
            .Concat(localModelPaths?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        var sb = new StringBuilder();
        if (global is not null && InferenceTuning.ValidateProfile(global) is null &&
            (!global.IsAutomatic || !string.IsNullOrEmpty(globalDeviceId)))
        {
            sb.Append("[*]\n");
            AppendOptions(sb, global, SafeDeviceId(globalDeviceId));
            sb.Append('\n');
        }
        foreach (var id in ids)
        {
            // A section name comes from a server model id. Do not let a
            // hand-edited settings file inject another INI section or key.
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
                continue;
            var ctx = 0;
            ModelPromptProcessingProfile? profile = null;
            var hasContext = contexts is not null && contexts.TryGetValue(id, out ctx) && ctx > 0;
            var hasProfile = profiles is not null && profiles.TryGetValue(id, out profile) &&
                profile is not null &&
                InferenceTuning.ValidateProfile(profile) is null &&
                !profile.IsAutomatic;
            string? modelPath = null;
            var hasLocalPath = localModelPaths is not null &&
                localModelPaths.TryGetValue(id, out modelPath) &&
                !string.IsNullOrWhiteSpace(modelPath) &&
                modelPath.IndexOfAny(['\r', '\n']) < 0;
            if (!hasContext && !hasProfile && !hasLocalPath) continue;

            sb.Append('[').Append(id).Append("]\n");
            if (hasLocalPath) sb.Append("model = ").Append(modelPath).Append('\n');
            if (hasContext) sb.Append("ctx-size = ")
                .Append(ctx.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            if (hasProfile)
            {
                string? deviceId = null;
                if (profile!.GpuDeviceName == "none") deviceId = "none";
                else if (!string.IsNullOrEmpty(profile.GpuDeviceName))
                    deviceIds?.TryGetValue(profile.GpuDeviceName, out deviceId);
                // A missing saved device is an explicit CPU fallback. Never
                // silently run on another GPU after an eGPU is unplugged.
                if (!string.IsNullOrEmpty(profile.GpuDeviceName) && deviceId is null)
                    deviceId = "none";
                AppendOptions(sb, profile, SafeDeviceId(deviceId));
            }
            sb.Append('\n');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? SafeDeviceId(string? value) =>
        value is null || value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ',' or '.')
            ? value : "none";

    private static void AppendOptions(StringBuilder sb, ModelPromptProcessingProfile p, string? deviceId)
    {
        static void Add(StringBuilder output, string key, object value) =>
            output.Append(key).Append(" = ").Append(Convert.ToString(value,
                System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        if (p.BatchSize > 0) Add(sb, "batch-size", p.BatchSize);
        if (p.MicroBatchSize > 0) Add(sb, "ubatch-size", p.MicroBatchSize);
        if (p.FlashAttention is "on" or "off") Add(sb, "flash-attn", p.FlashAttention);
        if (deviceId is not null) Add(sb, "device", deviceId);
        if (deviceId == "none") Add(sb, "n-gpu-layers", 0);
        else if (!string.IsNullOrEmpty(p.GpuLayers)) Add(sb, "n-gpu-layers", p.GpuLayers);
        if (!string.IsNullOrEmpty(p.CacheTypeK)) Add(sb, "cache-type-k", p.CacheTypeK);
        if (!string.IsNullOrEmpty(p.CacheTypeV)) Add(sb, "cache-type-v", p.CacheTypeV);
        if (p.Threads > 0) Add(sb, "threads", p.Threads);
        if (p.ThreadsBatch > 0) Add(sb, "threads-batch", p.ThreadsBatch);
        if (p.Parallel > 0) Add(sb, "parallel", p.Parallel);
        if (p.Temperature is { } temp) Add(sb, "temp", temp);
        if (p.TopK is { } topK) Add(sb, "top-k", topK);
        if (p.TopP is { } topP) Add(sb, "top-p", topP);
        if (p.RepeatPenalty is { } repeat) Add(sb, "repeat-penalty", repeat);
        if (!string.IsNullOrEmpty(p.SplitMode)) Add(sb, "split-mode", p.SplitMode);
        if (!string.IsNullOrEmpty(p.TensorSplit)) Add(sb, "tensor-split", p.TensorSplit);
    }
}
