namespace LlamaApp.Common;

/// <summary>
/// Free-disk-space probes used to preflight multi-GB model downloads before
/// they start. All methods are total: an unreadable path or drive returns
/// "unknown" rather than throwing, and callers treat "unknown" as "allow" —
/// a failed probe must never block a download.
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// Returns the free bytes on the drive hosting <paramref name="forPath"/>
    /// (which need not exist yet — the probe resolves the path's root).
    /// <c>false</c> when the drive can't be determined or queried.
    /// </summary>
    public static bool TryGetAvailableFreeBytes(string? forPath, out long freeBytes)
    {
        freeBytes = 0;
        if (string.IsNullOrWhiteSpace(forPath)) return false;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(forPath));
            if (string.IsNullOrEmpty(root)) return false;
            freeBytes = new DriveInfo(root).AvailableFreeSpace;
            return true;
        }
        catch
        {
            // Invalid path syntax, unready drive, security — all "unknown".
            return false;
        }
    }

    /// <summary>
    /// True when the drive hosting <paramref name="forPath"/> has at least
    /// <paramref name="neededBytes"/> free — or when the free space can't be
    /// determined (a failed probe never blocks). <paramref name="freeBytes"/>
    /// receives the probe result for messaging; 0 when unknown.
    /// </summary>
    public static bool HasEnoughSpace(string? forPath, ulong neededBytes, out long freeBytes)
    {
        if (!TryGetAvailableFreeBytes(forPath, out freeBytes)) return true;
        return freeBytes >= 0 && (ulong)freeBytes >= neededBytes;
    }
}
