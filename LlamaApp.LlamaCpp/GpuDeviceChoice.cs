namespace LlamaApp.Llama;

/// <summary>Names a GPU in settings and resolves it against the current backend probe.</summary>
public static class GpuDeviceChoice
{
    private const string IndexedPrefix = "gpu:";

    /// <summary>
    /// A unique model name survives device-index changes. Identical names need
    /// the current backend id to distinguish cards in the picker.
    /// </summary>
    public static string Key(LlamaDevice device, IReadOnlyList<LlamaDevice> devices) =>
        devices.Count(other => string.Equals(other.Name, device.Name,
            StringComparison.OrdinalIgnoreCase)) == 1
            ? device.Name
            : $"{IndexedPrefix}{device.Id}:{device.Name}";

    /// <summary>Returns null when a saved choice is missing or ambiguous.</summary>
    public static LlamaDevice? Resolve(string key, IReadOnlyList<LlamaDevice> devices)
    {
        if (key.StartsWith(IndexedPrefix, StringComparison.Ordinal))
        {
            var separator = key.IndexOf(':', IndexedPrefix.Length);
            if (separator < 0) return null;
            var id = key[IndexedPrefix.Length..separator];
            var name = key[(separator + 1)..];
            return devices.FirstOrDefault(device =>
                string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        var matches = devices.Where(device => string.Equals(device.Name, key,
            StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Snapshot of reported free and total memory for the picker.</summary>
    public static string Label(LlamaDevice device) =>
        $"{device.Name} ({device.Id}) · {MemoryFit.FormatBytes(device.FreeBytes)} free / " +
        $"{MemoryFit.FormatBytes(device.TotalBytes)} total";
}
