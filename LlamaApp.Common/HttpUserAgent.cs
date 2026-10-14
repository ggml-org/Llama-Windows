using System.Reflection;
using System.Runtime.InteropServices;

namespace LlamaApp.Common;

/// <summary>
/// The single HTTP User-Agent every <see cref="System.Net.Http.HttpClient"/>
/// the app creates must reuse:
/// <c>llama-win/<version> (<os version>; <arch>)</c> —
/// e.g. <c>llama-win/0.12.0 (10.0.26100; x64)</c> — so backends
/// (Hugging Face Hub, GitHub releases, the local llama-server) can tell our
/// traffic apart from curl, browsers, and other llama.cpp tooling, and see
/// which app version, Windows build, and machine architecture it came
/// from. This is the only usage signal the app sends: there is no telemetry.
///
/// <para>The architecture is the <b>machine's</b> native one, not the
/// process's: the app ships an x64 build, and on an ARM64 PC (Windows on
/// ARM) that x64 process runs under x64-on-ARM64 emulation. Emulation is
/// not WOW64: <c>GetNativeSystemInfo</c> reports AMD64, <c>IsWow64Process</c>
/// returns false, and the environment block says <c>AMD64</c> too (all
/// verified live) — every classic probe reports the emulated personality.
/// The one stable oracle is the machine's own environment value persisted
/// in the registry (<c>HKLM\...\Session Manager\Environment\PROCESSOR_ARCHITECTURE</c>,
/// written by the kernel at boot): <c>ARM64</c> on an ARM64 PC regardless of
/// the caller's bitness. A process-architecture token would report <c>x64</c>
/// for every ARM64 machine, hiding them from backend statistics.</para>
///
/// Computed once from the entry assembly's version (the app exe), so it
/// tracks the package version automatically; a library-only host (unit
/// tests) falls back to its own assembly version. Attach with
/// <c>client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent.Value)</c>.
/// </summary>
public static class HttpUserAgent
{
    private static readonly Lazy<string> Cached = new(() =>
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? typeof(HttpUserAgent).Assembly.GetName().Version
                      ?? new Version(0, 0, 0);
        var os = Environment.OSVersion.Version;
        var arch = NativeOsArchitecture().ToLowerInvariant();
        return $"llama-win/{version.ToString(3)} ({os.Major}.{os.Minor}.{os.Build}; {arch})";
    });

    /// <summary>The User-Agent header value, e.g. <c>llama-win/0.12.0 (10.0.26100; arm64)</c>.</summary>
    public static string Value => Cached.Value;

    /// <summary>
    /// The machine's native architecture: <c>x64</c>, <c>arm64</c>,
    /// <c>x86</c>, or <c>arm</c> (the historical 32-bit ARM). Reads the
    /// kernel-persisted machine environment value from the registry first —
    /// the only probe that pierces x64-on-ARM64 emulation (which presents a
    /// fully coherent AMD64 environment to every classic API) — falling back
    /// to <see cref="GetNativeSystemInfo"/>, then to the process
    /// architecture, so a non-Windows or degenerate host still yields a
    /// sensible token.
    /// </summary>
    internal static string NativeOsArchitecture()
    {
        try
        {
            // The kernel writes this value at boot from the OS image's own
            // personality — an emulated process inherits its view, but the
            // registry still holds the machine's truth.
            var raw = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "PROCESSOR_ARCHITECTURE", null) as string;
            if (string.Equals(raw, "ARM64", StringComparison.OrdinalIgnoreCase)) return "Arm64";
            if (string.Equals(raw, "AMD64", StringComparison.OrdinalIgnoreCase)) return "X64";
            if (string.Equals(raw, "x86", StringComparison.OrdinalIgnoreCase)) return "X86";
            if (string.Equals(raw, "ARM", StringComparison.OrdinalIgnoreCase)) return "Arm";
        }
        catch
        {
            // Unreadable registry — fall through to the system APIs.
        }

        try
        {
            GetNativeSystemInfo(out var info);
            return info.ProcessorArchitecture switch
            {
                9 => "X64",   // PROCESSOR_ARCHITECTURE_AMD64
                12 => "Arm64", // PROCESSOR_ARCHITECTURE_ARM64
                5 => "Arm",    // PROCESSOR_ARCHITECTURE_ARM
                0 => "X86",    // PROCESSOR_ARCHITECTURE_INTEL
                _ => RuntimeInformation.ProcessArchitecture.ToString(),
            };
        }
        catch
        {
            // Not a Windows host, or the call is unavailable — the process
            // architecture is the best remaining answer.
            return RuntimeInformation.ProcessArchitecture.ToString();
        }
    }

    /// <summary>The full SYSTEM_INFO layout — the API writes the entire
    /// struct (~48 bytes) regardless of how much the caller declares, so a
    /// truncated declaration is a stack buffer overrun (verified the hard
    /// way under x64 emulation). Only the leading architecture ushort is
    /// consumed.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_INFO
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public IntPtr MinimumApplicationAddress;
        public IntPtr MaximumApplicationAddress;
        public IntPtr ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    /// <summary>
    /// Reports the native architecture of the OS — unlike GetSystemInfo,
    /// not the one of the WOW64/emulated environment the calling process
    /// happens to run in.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern void GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);
}
