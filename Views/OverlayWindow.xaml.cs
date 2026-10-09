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
/// A spotlight-style prompt overlay: a centered, borderless Mica window that
/// hosts the running llama server's WebUI for the currently loaded model inside
/// a <see cref="Microsoft.UI.Xaml.Controls.WebView2"/>. Summoned by the global
/// <c>Alt+Space</c> hotkey (see <see cref="GlobalHotkey"/>) while Llama is
/// running.
///
/// <para>The overlay is created lazily on first summon and reused thereafter:
/// it hides rather than closes, mirroring the tray flyout's lifecycle, so
/// re-summoning is instant. Esc and deactivation (clicking away) dismiss it.</para>
///
/// <para>The embedded WebUI drives its own prompt + streaming chat against the
/// loaded model — the overlay just points a WebView2 at the right model URL
/// (<c>http://{ConnectAddress}:{ServerPort}?model={LoadedModelId}</c>, the same URL
/// <c>MainWindow</c> opens in the system browser) and lets it run. When no
/// model is loaded yet the base WebUI (router mode) is shown, which still lists
/// models; the header chip flips to amber + "No model".</para>
/// </summary>
public sealed partial class OverlayWindow : Window
{
    // Overlay covers ~60% of the active monitor's work-area width and ~70% of
    // its height so the embedded WebUI has room. Tunable: bump these toward 1.0
    // (or set fixed constants) to taste.
    private const double OverlayWidthFraction = 0.6;
    private const double OverlayHeightFraction = 0.7;
    // Clamp so the overlay never collapses to nothing on a tiny/rotated screen.
    // In DIPs, scaled by the target monitor's DPI at summon time — AppWindow
    // sizes are PHYSICAL pixels, so a raw pixel minimum would shrink to
    // nothing (in content terms) on high-DPI screens.
    private const int OverlayMinWidthDips = 520;
    private const int OverlayMinHeightDips = 360;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000L;

    private bool _configured;
    private IntPtr _hwnd;

    // URL the WebView2 is currently showing, so a re-summon re-navigates only
    // when the model id actually changed (preserves an in-flight chat). Null
    // until the first navigation lands.
    private Uri? _currentUri;

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
        AttachWebUiAuthHeader();
    }

    /// <summary>
    /// Makes the embedded WebUI work against an authenticated (non-loopback)
    /// server without putting the API key in a URL. llama.cpp's WebUI reads
    /// its key from a typed dialog — there is no query-parameter form — so a
    /// <c>?api_key=</c> would be ignored by the page and still leak into
    /// WebView caches, browser history and server logs. Instead, once the
    /// WebView2's CoreWebView2 comes up (CoreWebView2Initialized fires for
    /// the implicit initialization triggered by the first Source assignment),
    /// register a resource filter for the server origin and add the
    /// <c>Authorization</c> header to every request the WebUI makes — the
    /// splash never appears and the key never leaves the process's memory as
    /// a URL.
    /// </summary>
    private void AttachWebUiAuthHeader()
    {
        ModelWebUi.CoreWebView2Initialized += (sender, e) =>
        {
            if (e.Exception is not null)
            {
                Common.Log.Warn(e.Exception, "overlay webview: initialization failed");
                return;
            }

            var key = LlamaManager.Shared.ApiKey;
            var core = ModelWebUi.CoreWebView2;
            if (key is null || core is null) return; // loopback bind — no auth

            var origin =
                $"http://{LlamaManager.Shared.ConnectAddress}:{LlamaManager.Shared.ServerPort}/*";
            core.AddWebResourceRequestedFilter(
                origin, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, args) =>
                args.Request.Headers.SetHeader("Authorization", $"Bearer {key}");
            Common.Log.Info("overlay webview: API-key header attached for the authenticated WebUI");
        };
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

        // Pin the corner radius to the standard 8px "round" style rather than
        // relying on the system default.
        WindowCorners.ApplyRound8(this);
    }

    /// <summary>Shows the overlay centered on the monitor nearest the cursor, ready for input.</summary>
    public void Summon()
    {
        // Show the window FIRST, then populate content. Mutating a batch of
        // control properties on a frameless WS_POPUP Mica window before it has
        // been shown can trigger a layout pass on an un-shown window that faults
        // in the native WinUI layer (0xC0000005). The original working version
        // set only InputBox.IsEnabled before Show; we restore that order and do
        // all content updates after the window is on screen.
        Common.Log.Info("overlay summon: begin");
        CenterOnScreen();
        _lastShownMs = Environment.TickCount64;
        _suppressDeactivate = true;

        AppWindow.Show();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        Common.Log.Info("overlay summon: window shown");

        // Now that the window is on screen, refresh the chrome and navigate
        // the WebView2 to the loaded model's WebUI.
        RefreshModelBadge();
        NavigateToModel();

        Common.Log.Info("overlay summon: done");
    }

    /// <summary>
    /// Builds the URL the WebView2 should show: the running llama server's
    /// WebUI, scoped to the currently loaded model when one is resident. This
    /// mirrors <c>MainWindow.LocalModelOpen_Click</c>'s
    /// <c>?model=&lt;ServerModelId&gt;</c> pattern (passing <c>?model=</c> makes
    /// the server auto-load that model). With no model loaded we fall back to
    /// the bare WebUI — router mode hosts it even with no model, so the user
    /// can browse/download models from inside the overlay too.
    /// </summary>
    private static Uri BuildModelUri()
    {
        var baseUrl = $"http://{LlamaManager.Shared.ConnectAddress}:{LlamaManager.Shared.ServerPort}";
        var id = LlamaManager.Shared.LoadedModelId;
        // No api_key in the URL: the WebUI reads its key from a dialog (see
        // AttachWebUiAuthHeader — the header is injected per request instead).
        return id is null
            ? new Uri(baseUrl)
            : new Uri($"{baseUrl}?model={Uri.EscapeDataString(id)}");
    }

    /// <summary>
    /// Points the embedded WebView2 at <see cref="BuildModelUri"/>. Only
    /// re-navigates when the URL changes so a re-summon mid-chat doesn't
    /// reset it. Until the CoreWebView2 exists (and is therefore configured
    /// with the navigation policy), every navigation goes through
    /// <see cref="ConfigureWebViewThenNavigateAsync"/> — a bare Source
    /// assignment would trigger the WebView2 control's implicit
    /// initialization and render the first page with NO policy attached.
    /// </summary>
    private void NavigateToModel()
    {
        var uri = BuildModelUri();
        _desiredUri = uri;

        // "Unchanged" only counts while a page is actually live; after a
        // failed initialization (_currentUri set, CoreWebView2 null) this
        // path must retry, not skip.
        if (ModelWebUi.CoreWebView2 is not null &&
            _currentUri is not null && _currentUri == uri)
        {
            Common.Log.Info($"overlay webview: URL unchanged ({uri}), keeping current page");
            return;
        }

        _currentUri = uri;
        if (ModelWebUi.CoreWebView2 is null)
        {
            if (!_webViewInitStarted)
            {
                // Configure before the first navigation: the overlay is a
                // chromeless window with no address bar, so it must never end
                // up showing a page from anywhere but the local llama server.
                _webViewInitStarted = true;
                _ = ConfigureWebViewThenNavigateAsync();
            }
            // else: an initialization attempt is already in flight; it
            // navigates to the latest _desiredUri once it comes up.
            return;
        }

        ModelWebUi.Source = uri;
        Common.Log.Info($"overlay webview: navigating to {uri}");
    }

    private bool _webViewInitStarted;

    // The latest URI NavigateToModel wants shown. An in-flight initialization
    // navigates to this once the CoreWebView2 is ready, so a model change
    // during startup isn't lost.
    private Uri? _desiredUri;

    /// <summary>
    /// Initializes the WebView2's CoreWebView2, applies the navigation policy
    /// (see <see cref="Common.WebOriginPolicy"/>), then navigates to
    /// <see cref="_desiredUri"/>. Failures are logged and swallowed — a
    /// missing/broken WebView2 runtime must never fault the summon — and they
    /// reset the init flag so the next summon retries the configured path
    /// instead of falling back to an unconfigured implicit initialization.
    /// </summary>
    private async Task ConfigureWebViewThenNavigateAsync()
    {
        try
        {
            await ModelWebUi.EnsureCoreWebView2Async();
            var core = ModelWebUi.CoreWebView2;

#if !DEBUG
            // Release builds ship no developer surface.
            core.Settings.AreDevToolsEnabled = false;
#endif

            // Only the local llama server's own origin may display inside the
            // overlay — anything else (a link in the page, a redirect, a
            // squatter on the port) is cancelled. Top-level documents AND
            // child iframes: a page on the allowed origin could otherwise
            // embed a cross-origin frame in the chromeless window.
            core.NavigationStarting += (sender, e) => GuardNavigation(e);
            core.FrameNavigationStarting += (sender, e) => GuardNavigation(e);

            // New windows (target=_blank) go to the system browser, and only
            // for plain http(s) — never a custom scheme. (The sender is a
            // typed CoreWebView2 — don't reuse the `_` name for it here.)
            core.NewWindowRequested += (sender, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) &&
                    Common.WebOriginPolicy.IsOpenableExternal(target))
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(target);
                }
            };

            if (_desiredUri is not null)
            {
                ModelWebUi.Source = _desiredUri;
                Common.Log.Info($"overlay webview: navigating to {_desiredUri}");
            }
        }
        catch (Exception ex)
        {
            // Reset so the next summon retries the configured path — the
            // fallback must never be an unconfigured WebView.
            _webViewInitStarted = false;
            Common.Log.Warn(ex, "overlay webview: initialization failed");
        }
    }

    /// <summary>
    /// Cancels any document or frame navigation whose target is not the local
    /// llama server's origin — see <see cref="Common.WebOriginPolicy"/>.
    /// </summary>
    private void GuardNavigation(Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        Uri? target = null;
        try { Uri.TryCreate(e.Uri, UriKind.Absolute, out target); }
        catch { /* malformed — cancelled below */ }
        if (!Common.WebOriginPolicy.IsServerOrigin(
                target, LlamaManager.Shared.ConnectAddress, LlamaManager.Shared.ServerPort))
        {
            e.Cancel = true;
            Common.Log.Warn($"overlay webview: refused navigation to {e.Uri}");
        }
    }

    private void RefreshModelBadge()
    {
        var id = LlamaManager.Shared.LoadedModelId;
        var loaded = id is not null;
        ModelBadge.Text = loaded ? id! : "No model";
        StatusDot.Fill = loaded
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50))  // green
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23)); // amber
    }

    private void CenterOnScreen()
    {
        var area = GetCursorMonitorWorkArea();
        // The fraction of the work area is already in physical pixels (like
        // area itself); only the DIPs minimum needs scaling to this monitor's
        // DPI before the two can be compared.
        var scale = GetCursorMonitorDpiScale();
        var minW = (int)Math.Round(OverlayMinWidthDips * scale);
        var minH = (int)Math.Round(OverlayMinHeightDips * scale);
        var w = Math.Max(minW, (int)(area.Width * OverlayWidthFraction));
        var h = Math.Max(minH, (int)(area.Height * OverlayHeightFraction));
        AppWindow.Resize(new SizeInt32(w, h));
        var x = area.X + (area.Width - w) / 2;
        var y = area.Y + (area.Height - h) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// DPI scale (1.0 = 100%) of the monitor nearest the cursor — the monitor
    /// <see cref="CenterOnScreen"/> targets. Defaults to 1.0 if the query fails.
    /// </summary>
    private static double GetCursorMonitorDpiScale()
    {
        GetCursorPos(out var p);
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            ? dpiX / 96.0
            : 1.0;
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

    /// <summary>Esc accelerator (bound in XAML) dismisses the overlay.</summary>
    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Hide();
        args.Handled = true;
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

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int MDT_EFFECTIVE_DPI = 0;

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