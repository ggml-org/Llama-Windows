using System.Runtime.InteropServices;
using LlamaApp.Llama;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace LlamaApp.Views;

/// <summary>
/// A spotlight-style prompt overlay: a centered, borderless Mica window with a
/// single text input that streams a chat completion from the running llama
/// server's loaded model. Summoned by the global <c>Alt+Space</c> hotkey (see
/// <see cref="GlobalHotkey"/>) while LlamaApp is running.
///
/// <para>The overlay is created lazily on first summon and reused thereafter:
/// it hides rather than closes, mirroring the tray flyout's lifecycle, so
/// re-summoning is instant. Esc and deactivation (clicking away) dismiss it.</para>
///
/// <para>"Only works when the app is started" is enforced by the hotkey's
/// lifetime (registered on app start, unregistered on exit) and by the input
/// being disabled with a hint when no model is loaded yet.</para>
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 560;
    private const int OverlayHeight = 220;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000L;

    private CancellationTokenSource? _chatCts;
    private bool _configured;
    private bool _streaming;
    private IntPtr _hwnd;

    // Suppress the spurious deactivation that races ahead of (or lands just
    // after) the show sequence when Windows denies us foreground — without it
    // the overlay hides itself straight back, looking like it never opened.
    private const long ShownDeactivationGraceMs = 250;
    private bool _suppressDeactivate;
    private long _lastShownMs;

    public OverlayWindow()
    {
        InitializeComponent();
        ConfigureAsOverlay();
        Activated += OverlayWindow_Activated;
    }

    /// <summary>Style as a borderless, taskbar-less spotlight popup centered on screen.</summary>
    private void ConfigureAsOverlay()
    {
        if (_configured) return;
        _configured = true;

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false; // keep out of Alt-Tab / taskbar
        AppWindow.Resize(new SizeInt32(OverlayWidth, OverlayHeight));

        _hwnd = WindowNative.GetWindowHandle(this);

        // WS_EX_TOOLWINDOW: no taskbar entry. WS_POPUP: drop the 1px frame so
        // the Mica surface is flush to the rounded corners (same trick the
        // tray flyout uses).
        SetWindowLongCompat(_hwnd, GWL_EXSTYLE,
            (IntPtr)(GetWindowLongCompat(_hwnd, GWL_EXSTYLE).ToInt32() | WS_EX_TOOLWINDOW));
        SetWindowLongCompat(_hwnd, GWL_STYLE, new IntPtr(WS_POPUP));

        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
                   SWP_NOZORDER = 0x0004, SWP_NOOWNERZORDER = 0x0200,
                   SWP_FRAMECHANGED = 0x0020;
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Shows the overlay centered on the monitor nearest the cursor, ready for input.</summary>
    public void Summon()
    {
        CenterOnScreen();
        _lastShownMs = Environment.TickCount64;
        _suppressDeactivate = true;
        RefreshModelBadge();

        // Clear any stale response from a prior session.
        ResponseText.Text = string.Empty;
        InputBox.Text = string.Empty;
        InputBox.IsEnabled = LlamaManager.Shared.LoadedModelId is not null;

        AppWindow.Show();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        _ = InputBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void RefreshModelBadge()
    {
        var id = LlamaManager.Shared.LoadedModelId;
        ModelBadge.Text = id is null
            ? "No model loaded — load one from the flyout first."
            : $"Model: {id}";
        InputBox.PlaceholderText = id is null
            ? "No model loaded"
            : "Ask the loaded model…";
    }

    private void CenterOnScreen()
    {
        AppWindow.Resize(new SizeInt32(OverlayWidth, OverlayHeight));
        var area = GetCursorMonitorWorkArea();
        var x = area.X + (area.Width - OverlayWidth) / 2;
        var y = area.Y + (area.Height - OverlayHeight) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    private RectInt32 GetCursorMonitorWorkArea()
    {
        // Place near the cursor's monitor so the spotlight follows the user
        // across multi-monitor setups (like macOS spotlight on the active space).
        GetCursorPos(out var p);
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(mon, ref info);
        return new RectInt32(info.rcWork.Left, info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top);
    }

    // ---- send / stream ----

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        if (e.Key != Windows.System.VirtualKey.Enter) return;

        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _streaming) { e.Handled = true; return; }
        if (LlamaManager.Shared.LoadedModelId is null) { e.Handled = true; return; }

        e.Handled = true;
        _ = SendAsync(text);
    }

    private async Task SendAsync(string message)
    {
        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        var token = _chatCts.Token;

        _streaming = true;
        InputBox.IsEnabled = false;
        ResponseText.Text = string.Empty;

        try
        {
            await foreach (var chunk in LlamaManager.Shared.StreamChatAsync(message, token))
                ResponseText.Text += chunk;
        }
        catch (OperationCanceledException) { /* user started a new prompt or dismissed */ }
        catch (Exception ex)
        {
            ResponseText.Text = $"⚠ {ex.Message}";
            Common.Log.Warn(ex, "overlay chat completion failed");
        }
        finally
        {
            _streaming = false;
            RefreshModelBadge();
            InputBox.IsEnabled = LlamaManager.Shared.LoadedModelId is not null;
            InputBox.Text = string.Empty;
        }
    }

    private void OverlayWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Dismiss on deactivation (clicking away), the same UX as the tray flyout.
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_suppressDeactivate ||
                Environment.TickCount64 - _lastShownMs <= ShownDeactivationGraceMs)
            {
                _suppressDeactivate = false;
                return;
            }
            Hide();
        }
        else
        {
            _suppressDeactivate = false;
        }
    }

    private void Hide()
    {
        _chatCts?.Cancel();
        AppWindow.Hide();
        ShowWindow(_hwnd, SW_HIDE);
    }

    // ---- Win32 ----

    private const int SW_SHOW = 5, SW_HIDE = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int nIndex, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int nIndex, IntPtr value);

    private static IntPtr GetWindowLongCompat(IntPtr hwnd, int nIndex) =>
        IntPtr.Size == 4 ? (IntPtr)GetWindowLong32(hwnd, nIndex) : GetWindowLongPtr64(hwnd, nIndex);

    private static void SetWindowLongCompat(IntPtr hwnd, int nIndex, IntPtr value)
    {
        if (IntPtr.Size == 4) SetWindowLong32(hwnd, nIndex, value.ToInt32());
        else SetWindowLongPtr64(hwnd, nIndex, value);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
