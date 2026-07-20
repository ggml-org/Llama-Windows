using System.Runtime.InteropServices;
using LlamaApp.Common;

namespace LlamaApp;

/// <summary>
/// Registers a system-wide hotkey while LlamaApp is running, mirroring the
/// macOS spotlight/Raycast summon. Uses a tiny message-only Win32 window
/// (created on the UI thread, whose message loop dispatches the resulting
/// <c>WM_HOTKEY</c>) so we don't need to subclass any WinUI window's WndProc.
///
/// <para>"Only works when the app is started" falls out naturally: the hotkey
/// is registered on <see cref="Register"/> and unregistered on
/// <see cref="Dispose"/>, so it is live exactly for the app's lifetime.</para>
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x0001;

    // MOD_ALT + MOD_NOREPEAT, VK_SPACE → Alt+Space (the spotlight convention,
    // matching PowerToys Run's default). The global registration intercepts it
    // system-wide, so the Win11 system-menu shortcut is shadowed only while
    // the app is running.
    private const uint Modifier = 0x0001 /*MOD_ALT*/ | 0x4000 /*MOD_NOREPEAT*/;
    private const uint Vk = 0x20; // VK_SPACE

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // Single-slot pinning (one hotkey window per app). A GC handle keeps the
    // delegate alive across the native boundary; the static WndProc looks it up.
    private static WndProc? _current;
    private static GCHandle _currentHandle;

    private IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;

    /// <summary>Registers the hotkey and delivers presses to <paramref name="onPressed"/>.</summary>
    public void Register(Action onPressed)
    {
        if (_registered) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        _current = (hwnd, msg, wParam, lParam) =>
        {
            if (msg == WM_HOTKEY)
            {
                onPressed();
                return IntPtr.Zero;
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        };
        _currentHandle = GCHandle.Alloc(_current);

        _hwnd = CreateMessageOnlyWindow();
        _registered = RegisterHotKey(_hwnd, HotkeyId, Modifier, Vk);
        if (!_registered)
            Log.Warn("Global hotkey registration failed (Alt+Space may already be taken by another app).");
        else
            Log.Info("Global hotkey registered: Alt+Space");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_currentHandle.IsAllocated)
        {
            _currentHandle.Free();
            _current = null;
        }
    }

    private static IntPtr CreateMessageOnlyWindow()
    {
        var className = "LlamaAppHotkeyWnd_" + Guid.NewGuid().ToString("N");
        var fnPtr = Marshal.GetFunctionPointerForDelegate(_current!);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = fnPtr,
            lpszClassName = className,
        };
        RegisterClassExW(ref wc);

        var hwnd = CreateWindowExW(
            0, className, "LlamaAppHotkey", 0,
            0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return hwnd;
    }

    // Window procedure MUST return LRESULT (IntPtr); unhandled messages defer
    // to DefWindowProc so creation messages (WM_NCCREATE etc.) succeed.
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW([In] ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
