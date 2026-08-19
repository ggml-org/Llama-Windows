using System.Runtime.InteropServices;

namespace LlamaApp;

/// <summary>
/// Available physical memory, used by the model details view to gray out
/// context lengths whose estimated footprint (weights + KV cache) can't
/// currently fit in RAM.
/// </summary>
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Free physical RAM in bytes. <see cref="long.MaxValue"/> when the probe
    /// fails — treat every option as fitting rather than graying out the
    /// world on a measurement error.
    /// </summary>
    public static long AvailablePhysicalBytes()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : long.MaxValue;
        }
        catch
        {
            return long.MaxValue;
        }
    }
}
