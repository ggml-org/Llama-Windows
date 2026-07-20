using System.Runtime.InteropServices;
using LlamaApp.Llama;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
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
    private const int OverlayWidth = 760;
    private const int OverlayHeight = 440;

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
        // Show the window FIRST, then populate content. Mutating a batch of
        // control properties (Visibility / IsEnabled / Fill / Text) on a
        // frameless WS_POPUP Mica window before it has been shown can trigger a
        // layout pass on an un-shown window that faults in the native WinUI
        // layer (0xC0000005). The original working version set only
        // InputBox.IsEnabled before Show; we restore that order and do all
        // content updates after the window is on screen.
        Common.Log.Info("overlay summon: begin");
        CenterOnScreen();
        _lastShownMs = Environment.TickCount64;
        _suppressDeactivate = true;

        AppWindow.Show();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        Common.Log.Info("overlay summon: window shown");

        // Now that the window is on screen, refresh the chrome and content.
        RefreshModelBadge();
        ResponseText.Text = string.Empty;
        SetResponseChrome(streaming: false, empty: true);
        InputBox.Text = string.Empty;

        _ = InputBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Common.Log.Info("overlay summon: done");
    }

    private void RefreshModelBadge()
    {
        var id = LlamaManager.Shared.LoadedModelId;
        var loaded = id is not null;
        ModelBadge.Text = loaded ? id! : "No model";
        StatusDot.Fill = loaded
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50))  // green
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23)); // amber

        InputBox.PlaceholderText = loaded ? "Ask the loaded model…" : "No model loaded";
        InputBox.IsEnabled = loaded;
        SendButton.IsEnabled = loaded && !_streaming;

        // Empty-state copy reflects whether a model is available.
        if (loaded)
        {
            PlaceholderTitle.Text = "Ask anything";
            PlaceholderSubtitle.Text = "Your answer will stream here.";
        }
        else
        {
            PlaceholderTitle.Text = "No model loaded";
            PlaceholderSubtitle.Text = "Load a model from the flyout to start asking.";
        }
    }

    /// <summary>
    /// Toggles the streaming indicator and the empty-state placeholder. The
    /// streaming indicator wins when active; the placeholder shows only when
    /// the response is empty and nothing is streaming.
    /// </summary>
    private void SetResponseChrome(bool streaming, bool empty)
    {
        StreamingIndicator.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
        ResponsePlaceholder.Visibility = (empty && !streaming) ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Send button = Enter. Same guards as the key handler.</summary>
    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _streaming) return;
        if (LlamaManager.Shared.LoadedModelId is null) return;
        _ = SendAsync(text);
    }

    private async Task SendAsync(string message)
    {
        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        var token = _chatCts.Token;

        _streaming = true;
        SendButton.IsEnabled = false;
        InputBox.IsEnabled = false;
        ResponseText.Text = string.Empty;
        SetResponseChrome(streaming: true, empty: false);
        // The LlamaManager.StreamChatAsync call below logs the model id, the
        // HTTP response status, and the final chunk count, so the overlay log
        // only needs to record that a send started (for correlation).
        Common.Log.Info("overlay send: " + message.Length + " chars");

        try
        {
            var firstChunk = true;
            await foreach (var chunk in LlamaManager.Shared.StreamChatAsync(message, token))
            {
                if (firstChunk)
                {
                    StreamingIndicator.Visibility = Visibility.Collapsed;
                    firstChunk = false;
                }
                ResponseText.Text += chunk;
                // Keep the latest content in view as it streams in. Scroll to
                // the actual bottom (ScrollableHeight) rather than
                // double.MaxValue, which can overflow native scroll math and
                // fault (0xC0000005).
                ResponseScroll.ChangeView(null, ResponseScroll.ScrollableHeight, null);
            }
        }
        catch (OperationCanceledException)
        {
            // User started a new prompt or dismissed the overlay. If the
            // overlay is still visible and nothing was streamed, the
            // cancellation was unexpected — surface it so it doesn't silently
            // read as "nothing happened" (which was the original bug report).
            if (ResponseText.Text.Length == 0 && AppWindow.IsVisible)
            {
                Common.Log.Warn("overlay chat cancelled before any text (overlay still visible)");
                ResponseText.Text = "⚠ Request was cancelled before any response. Try again.";
            }
        }
        catch (Exception ex)
        {
            ResponseText.Text = $"⚠ {ex.Message}";
            Common.Log.Warn(ex, "overlay chat completion failed");
        }
        finally
        {
            _streaming = false;
            RefreshModelBadge(); // re-enables SendButton/InputBox per model state
            SetResponseChrome(streaming: false, empty: string.IsNullOrEmpty(ResponseText.Text));
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