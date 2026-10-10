namespace LlamaApp.Llama;

/// <summary>Locates the Windows HIP SDK libraries needed by ROCm llama binaries.</summary>
internal static class HipSdkEnvironment
{
    private static readonly object Gate = new();

    internal static void EnsureOnProcessPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var bin = FindBin();
        if (bin is null) return;

        lock (Gate)
        {
            var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (current.Split(Path.PathSeparator).Any(part =>
                string.Equals(part.TrimEnd(Path.DirectorySeparatorChar),
                    bin.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                return;

            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + current);
            Common.Log.Info($"using HIP SDK libraries from {bin}");
        }
    }

    internal static string? FindBin()
    {
        var candidates = new List<string>();
        foreach (var name in new[] { "HIP_PATH", "ROCM_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            candidates.Add(value);
            candidates.Add(Path.Combine(value, "bin"));
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AMD", "ROCm");
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(root)
                .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version)
                    ? version : new Version(0, 0))
                .Select(path => Path.Combine(path, "bin")));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "hipblas.dll")));
    }
}
