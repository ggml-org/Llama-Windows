using System.Diagnostics;

namespace LlamaApp.Llama;

/// <summary>Validated optional llama.cpp prompt-processing controls.</summary>
public static class InferenceTuning
{
    public const int MaxBatchSize = 8192;

    /// <summary>Validate all preset values, including hand-edited settings JSON.</summary>
    public static string? ValidateProfile(ModelPromptProcessingProfile profile)
    {
        if (Validate(profile.BatchSize, profile.MicroBatchSize, profile.FlashAttention) is { } error)
            return error;
        if (!string.IsNullOrEmpty(profile.GpuLayers) && profile.GpuLayers != "auto" &&
            InferenceRuntime.GpuLayersArgument(profile.GpuLayers) is null)
            return "GPU layers must be Automatic, All, or a count from 0 to 1000.";
        if (!string.IsNullOrEmpty(profile.CacheTypeK) &&
            !LlamaManager.SupportedKvCacheTypes.Contains(profile.CacheTypeK, StringComparer.Ordinal))
            return "Unsupported key cache type.";
        if (!string.IsNullOrEmpty(profile.CacheTypeV) &&
            !LlamaManager.SupportedKvCacheTypes.Contains(profile.CacheTypeV, StringComparer.Ordinal))
            return "Unsupported value cache type.";
        if (profile.Threads is < 0 or > 1024 || profile.ThreadsBatch is < 0 or > 1024)
            return "Thread counts must be Automatic or between 1 and 1024.";
        if (profile.Parallel is < 0 or > 256)
            return "Parallel slots must be Automatic or between 1 and 256.";
        if (profile.Temperature is { } temp && (!double.IsFinite(temp) || temp is < 0 or > 5))
            return "Temperature must be between 0 and 5.";
        if (profile.TopK is { } topK && topK is < 0 or > 10000)
            return "Top K must be between 0 and 10000.";
        if (profile.TopP is { } topP && (!double.IsFinite(topP) || topP is < 0 or > 1))
            return "Top P must be between 0 and 1.";
        if (profile.RepeatPenalty is { } repeat && (!double.IsFinite(repeat) || repeat is < 0 or > 5))
            return "Repeat penalty must be between 0 and 5.";
        if (profile.SplitMode is not (null or "" or "none" or "layer" or "row" or "tensor"))
            return "Split mode must be Automatic, None, Layer, Row, or Tensor.";
        if (!string.IsNullOrEmpty(profile.TensorSplit))
        {
            var values = profile.TensorSplit.Split(',');
            if (values.Length is < 2 or > 16 || values.Any(value =>
                !double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ratio) ||
                !double.IsFinite(ratio) || ratio <= 0 || ratio > 1000))
                return "Tensor split needs 2–16 positive comma-separated weights.";
        }
        if (profile.GpuDeviceName?.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
            return "GPU name contains invalid characters.";
        return null;
    }

    public static string? UnsupportedProfileOption(ModelPromptProcessingProfile profile,
        ServeCapabilities capabilities)
    {
        if (!capabilities.Succeeded) return "Could not check the installed llama server's advanced options.";
        string? Check(bool enabled, string flag) => enabled && !capabilities.Supports(flag) ? flag : null;
        return Check(profile.BatchSize > 0, "--batch-size")
            ?? Check(profile.MicroBatchSize > 0, "--ubatch-size")
            ?? Check(profile.FlashAttention is "on" or "off", "--flash-attn")
            ?? Check(!string.IsNullOrEmpty(profile.GpuDeviceName), "--device")
            ?? Check(!string.IsNullOrEmpty(profile.GpuLayers), "--n-gpu-layers")
            ?? Check(!string.IsNullOrEmpty(profile.CacheTypeK), "--cache-type-k")
            ?? Check(!string.IsNullOrEmpty(profile.CacheTypeV), "--cache-type-v")
            ?? Check(profile.Threads > 0, "--threads")
            ?? Check(profile.ThreadsBatch > 0, "--threads-batch")
            ?? Check(profile.Parallel > 0, "--parallel")
            ?? Check(profile.Temperature is not null, "--temp")
            ?? Check(profile.TopK is not null, "--top-k")
            ?? Check(profile.TopP is not null, "--top-p")
            ?? Check(profile.RepeatPenalty is not null, "--repeat-penalty")
            ?? Check(!string.IsNullOrEmpty(profile.SplitMode), "--split-mode")
            ?? Check(!string.IsNullOrEmpty(profile.TensorSplit), "--tensor-split");
    }

    internal static IReadOnlyDictionary<string, ModelPromptProcessingProfile> CompatibleModelProfiles(
        IReadOnlyDictionary<string, ModelPromptProcessingProfile>? profiles,
        int globalBatchSize, int globalMicroBatchSize, ServeCapabilities capabilities,
        out IReadOnlyList<string> warnings)
    {
        var compatible = new Dictionary<string, ModelPromptProcessingProfile>(StringComparer.Ordinal);
        var omitted = new List<string>();
        if (profiles is not null)
        {
            foreach (var (id, profile) in profiles)
            {
                if (profile is null) continue;
                var error = ValidateProfile(profile) ??
                    ValidateEffective(globalBatchSize, globalMicroBatchSize,
                        profile.BatchSize, profile.MicroBatchSize, profile.FlashAttention);
                if (error is null && !profile.IsAutomatic)
                {
                    var unsupported = UnsupportedProfileOption(profile, capabilities);
                    if (unsupported is not null)
                        error = unsupported.StartsWith("--", StringComparison.Ordinal)
                            ? $"The installed llama server does not support {unsupported}."
                            : unsupported;
                }
                if (error is null) compatible[id] = profile;
                else omitted.Add($"Model {id}: {error}");
            }
        }
        warnings = omitted;
        return compatible;
    }

    public static string? Validate(int batchSize, int microBatchSize, string? flashAttention)
    {
        if (batchSize < 0 || batchSize > MaxBatchSize)
            return $"Batch size must be between 1 and {MaxBatchSize}, or Automatic.";
        if (microBatchSize < 0 || microBatchSize > MaxBatchSize)
            return $"Microbatch size must be between 1 and {MaxBatchSize}, or Automatic.";
        if (batchSize > 0 && microBatchSize > batchSize)
            return "Microbatch size cannot exceed batch size.";
        if (flashAttention is not (null or "auto" or "on" or "off"))
            return "Flash Attention must be Automatic, On, or Off.";
        return null;
    }

    public static bool HasOverrides(int batchSize, int microBatchSize, string? flashAttention)
        => batchSize > 0 || microBatchSize > 0 || flashAttention is "on" or "off";

    /// <summary>Checks a model profile after it inherits unset global sizes.</summary>
    public static string? ValidateEffective(
        int globalBatchSize, int globalMicroBatchSize,
        int modelBatchSize, int modelMicroBatchSize, string? modelFlashAttention)
    {
        var ownError = Validate(modelBatchSize, modelMicroBatchSize, modelFlashAttention);
        if (ownError is not null) return ownError;
        return Validate(modelBatchSize > 0 ? modelBatchSize : globalBatchSize,
            modelMicroBatchSize > 0 ? modelMicroBatchSize : globalMicroBatchSize,
            modelFlashAttention);
    }

    public static string? UnsupportedOption(
        int batchSize, int microBatchSize, string? flashAttention, ServeCapabilities capabilities)
    {
        if (!capabilities.Succeeded)
            return "Could not check the installed llama server's advanced options.";
        if (batchSize > 0 && !capabilities.BatchSize) return "--batch-size";
        if (microBatchSize > 0 && !capabilities.MicroBatchSize) return "--ubatch-size";
        if (flashAttention is "on" or "off" && !capabilities.FlashAttention)
            return "--flash-attn";
        return null;
    }
}

/// <summary>Flags advertised by the selected binary's <c>serve --help</c>.</summary>
public sealed record ServeCapabilities(bool Succeeded, bool BatchSize, bool MicroBatchSize,
    bool FlashAttention, string HelpText = "")
{
    public bool Supports(string flag) => Succeeded &&
        System.Text.RegularExpressions.Regex.IsMatch(HelpText,
            @"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(flag) + @"(?=[\s,]|$)");

    internal static ServeCapabilities Parse(string? output)
    {
        // The Windows launcher prints a parameter listing without a "Usage:"
        // header. Require both common and server-specific markers so an error
        // such as "unknown command" is not mistaken for valid help output.
        if (string.IsNullOrWhiteSpace(output) ||
            !output.Contains("--help", StringComparison.Ordinal) ||
            !output.Contains("--port", StringComparison.Ordinal))
            return new(false, false, false, false);
        return new(true,
            output.Contains("--batch-size", StringComparison.Ordinal),
            output.Contains("--ubatch-size", StringComparison.Ordinal),
            output.Contains("--flash-attn", StringComparison.Ordinal), output);
    }

    public static async Task<ServeCapabilities> ProbeAsync(string binaryPath, CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--help");

        try
        {
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return Parse(null);
            var stdout = proc.StandardOutput.ReadToEndAsync(cancel);
            var stderr = proc.StandardError.ReadToEndAsync(cancel);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Parse(null);
            }
            return Parse(await stdout + "\n" + await stderr);
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            return Parse(null);
        }
    }
}
