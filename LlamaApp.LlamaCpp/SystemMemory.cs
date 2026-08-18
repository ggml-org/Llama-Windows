using System.Runtime.InteropServices;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// System physical-memory probe — the fallback for model-fit decisions when
/// <c>llama --list-devices</c> reports no accelerator devices: the model then
/// runs on the CPU and must fit in RAM. Wraps the Win32
/// <c>GlobalMemoryStatusEx</c> API; total (never throws — a failed probe
/// returns <c>false</c> and callers treat "unknown" as "allow", matching the
/// fail-open convention of <see cref="DiskSpace"/>).
/// </summary>
public static class SystemMemory
{
    /// <summary>
    /// Returns the machine's total and currently-available physical memory in
    /// bytes. <c>false</c> when the OS call fails (in which case both outputs
    /// are 0) — callers must not block on an unknown answer.
    /// </summary>
    public static bool TryGet(out ulong totalBytes, out ulong availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
                return false;

            totalBytes = status.ullTotalPhys;
            availableBytes = status.ullAvailPhys;
            return totalBytes > 0;
        }
        catch
        {
            return false;
        }
    }

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
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
