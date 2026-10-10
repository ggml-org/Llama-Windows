namespace LlamaApp.Llama;

/// <summary>Backend chosen for the app-managed llama.cpp binary.</summary>
public static class InferenceRuntime
{
    public const string Automatic = "auto";
    public const string Rocm = "rocm";
    public const string Vulkan = "vulkan";
    public const string Cpu = "cpu";

    public static string Normalize(string? value) => value?.ToLowerInvariant() switch
    {
        Rocm => Rocm,
        Vulkan => Vulkan,
        Cpu => Cpu,
        _ => Automatic,
    };

    /// <summary>
    /// The official Windows installer tries CUDA, ROCm, Vulkan, then CPU.
    /// SKIP_* variables narrow that choice while retaining its CPU fallback.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> InstallerEnvironment(string? backend)
    {
        var skips = new Dictionary<string, string>();
        switch (Normalize(backend))
        {
            case Rocm:
                skips["SKIP_CUDA"] = "1";
                skips["SKIP_VULKAN"] = "1";
                break;
            case Vulkan:
                skips["SKIP_CUDA"] = "1";
                skips["SKIP_ROCM"] = "1";
                break;
            case Cpu:
                skips["SKIP_CUDA"] = "1";
                skips["SKIP_ROCM"] = "1";
                skips["SKIP_VULKAN"] = "1";
                break;
        }
        return skips;
    }

    internal static bool RequiresManagedInstall(string? requested, string? installed)
        => Normalize(requested) != Normalize(installed);

    /// <summary>Resolve a saved GPU choice to the current llama.cpp device id.</summary>
    public static DeviceSelection SelectDevice(string? backend, string? gpuName, DeviceProbe probe)
    {
        var requested = Normalize(backend);
        if (requested == Cpu)
            return new DeviceSelection("none", null);
        if (requested == Automatic && string.IsNullOrWhiteSpace(gpuName))
            return new DeviceSelection(null, null); // Keep llama.cpp's defaults.
        if (!probe.Succeeded)
            return new DeviceSelection("none", "GPU detection failed; using the CPU.");

        var candidates = probe.Devices.Where(device => requested switch
        {
            Rocm => device.Kind == DeviceKind.Rocm,
            Vulkan => device.Kind == DeviceKind.Vulkan,
            _ => device.Kind != DeviceKind.Cpu && device.Kind != DeviceKind.Unknown,
        }).ToArray();

        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            var match = GpuDeviceChoice.Resolve(gpuName, candidates);
            return match is null
                ? new DeviceSelection("none", $"{gpuName} is unavailable; using the CPU.")
                : new DeviceSelection(match.Id, null);
        }

        return candidates.Length == 0
            ? new DeviceSelection("none", $"No {requested.ToUpperInvariant()} GPU is available; using the CPU.")
            : new DeviceSelection(string.Join(',', candidates.Select(device => device.Id)), null);
    }

    /// <summary>Validated --n-gpu-layers value; null means llama.cpp's automatic default.</summary>
    internal static string? GpuLayersArgument(string? value)
    {
        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return "all";
        return int.TryParse(value, out var count) && count is >= 0 and <= 1000
            ? count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}

/// <summary>Resolved CLI device and any reason for a CPU fallback.</summary>
public sealed record DeviceSelection(string? DeviceArgument, string? FallbackReason);
