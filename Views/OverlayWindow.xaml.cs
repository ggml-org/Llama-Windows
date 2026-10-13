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

    // UI thread queue — the shared-prompt retry needs to come back here
    // (WebView2 calls must stay on the UI thread).
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("OverlayWindow must be created on the UI thread.");

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

    // How long the CURRENT summon ignores deactivations (see Summon): the
    // hotkey default is ShownDeactivationGraceMs; programmatic summons pass a
    // longer grace to survive the dismissing share sheet's focus churn.
    private long _hideGraceMs = ShownDeactivationGraceMs;
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

    // ---- Shared-payload delivery (Windows share sheet) ----

    // A payload handed over by App (a Windows share) waiting for the WebUI to
    // be able to take it. Injection retries: a share can land before any model
    // is loaded, when the WebUI shows its model picker and there is no
    // composer yet — the payload waits and lands once the chat screen is up.
    // A shared image additionally waits for an image-capable model (the
    // paste targets the attachment pipeline, which needs vision).
    private sealed record PendingShare(string? Prompt, string? ImagePath);

    private PendingShare? _pendingShare;
    private int _shareAttempts;
    private const int MaxShareAttempts = 40; // ~2 min at 3s
    private bool _imageWaitNotified;

    // Retry chains are generation-guarded: a second share while the first is
    // still retrying must invalidate the first chain (otherwise both chains
    // share one attempt counter and give up in half the intended time —
    // observed in the logs as a give-up after ~1 minute instead of ~2).
    private int _shareGeneration;

    /// <summary>
    /// Queues shared text for the WebUI's chat composer. Attempts injection
    /// immediately and keeps retrying on a timer until a composer exists
    /// (the model picker screen has none) or the attempts run out.
    /// </summary>
    public void SetPendingPrompt(string prompt)
    {
        _pendingShare = new PendingShare(prompt, null);
        _shareAttempts = 0;
        _imageWaitNotified = false;
        _shareGeneration++;
        TryInjectPendingShareAsync();
    }

    /// <summary>
    /// Queues a shared image (.jpg/.jpeg/.png) as a synthetic paste into the
    /// WebUI's composer — its <c>onPaste</c> handler feeds pasted files to the
    /// attachment pipeline. The paste waits for an image-capable model to be
    /// loaded (a text-only model's chat accepts no attachments), retrying
    /// like a text prompt until then.
    /// </summary>
    public void SetPendingImage(string imagePath)
    {
        _pendingShare = new PendingShare(null, imagePath);
        _shareAttempts = 0;
        _imageWaitNotified = false;
        _shareGeneration++;
        TryInjectPendingShareAsync();
    }

    private async void TryInjectPendingShareAsync()
    {
        var pending = _pendingShare;
        if (pending is null) return;

        // Everything inside try: the CoreWebView2 getter can itself fault
        // while initialization is in flight, and an async void has no
        // catch-all — an escape here fail-fasts the process.
        try
        {
            var core = ModelWebUi.CoreWebView2;
            if (core is null)
            {
                RetryShareSoon(pending);
                return;
            }

            if (pending.ImagePath is { } imagePath)
            {
                // The attachment pipeline is only wired up for image-capable
                // models — wait (and say so, once) rather than pasting into a
                // chat that can't take the image.
                if (!LlamaManager.Shared.LoadedModelSupportsImages)
                {
                    if (!_imageWaitNotified)
                    {
                        _imageWaitNotified = true;
                        Notifications.Show("Waiting for an image-capable model",
                            "The shared image will attach once a model that supports images is loaded — pick one in the chat.");
                    }
                    RetryShareSoon(pending);
                    return;
                }

                var script = OverlayPromptInjection.BuildImagePaste(
                    Convert.ToBase64String(File.ReadAllBytes(imagePath)),
                    Common.SharedImage.MimeTypeFor(imagePath),
                    System.IO.Path.GetFileName(imagePath));
                var result = await core.ExecuteScriptAsync(script);
                if (bool.TryParse(result, out var attached) && attached)
                {
                    _pendingShare = null;
                    Common.Log.Info("overlay webview: shared image pasted into the chat composer");
                    DeleteOwnScratchImage(imagePath);
                    return;
                }
            }
            else if (pending.Prompt is { } prompt)
            {
                var result = await core.ExecuteScriptAsync(OverlayPromptInjection.Build(prompt));
                // ExecuteScriptAsync returns the script's result JSON-encoded —
                // a boolean comes back as the literal "true".
                if (bool.TryParse(result, out var placed) && placed)
                {
                    _pendingShare = null;
                    Common.Log.Info("overlay webview: shared prompt placed in the chat composer");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Common.Log.Debug($"overlay webview: share delivery not ready ({ex.Message})");
        }
        RetryShareSoon(pending);
    }

    /// <summary>Another attempt in a few seconds — the composer may not exist
    /// yet (no model loaded), the page may still be loading, or (for images)
    /// the loaded model may not accept images.</summary>
    private void RetryShareSoon(PendingShare pending)
    {
        if (_pendingShare is null) return;
        if (++_shareAttempts >= MaxShareAttempts)
        {
            Common.Log.Warn(
                (pending.ImagePath is { } path
                    ? $"gave up delivering the shared image '{path}'"
                    : $"gave up placing the shared prompt ({pending.Prompt?.Length ?? 0} chars)") +
                " — the chat never became ready for it");
            _pendingShare = null;
            if (pending.ImagePath is { } abandoned) DeleteOwnScratchImage(abandoned);
            return;
        }
        var generation = _shareGeneration;
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(
            _ =>
            {
                if (generation != _shareGeneration) return; // superseded by a newer share
                _dispatcher.TryEnqueue(TryInjectPendingShareAsync);
            });
    }

    /// <summary>
    /// The app's scratch dir for converted Bitmap shares — the only images
    /// Llama owns (a StorageItems share points at the user's own file, which
    /// must not be deleted).
    /// </summary>
    internal static string ScratchImageDir =>
        System.IO.Path.Combine(Common.AppData.Root, "shared");

    private static void DeleteOwnScratchImage(string path)
    {
        try
        {
            if (System.IO.Path.GetFullPath(path).StartsWith(
                    System.IO.Path.GetFullPath(ScratchImageDir) +
                    System.IO.Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
        catch { /* best-effort scratch cleanup */ }
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
    /// <param name="hideGraceMs">How long after the summon a focus loss (deactivation)
    /// is ignored instead of hiding the overlay. The hotkey summon keeps the
    /// default; programmatic summons (a share/Open With handoff) pass a longer
    /// grace — the dismissing share sheet races the summon for focus and can
    /// activate-then-deactivate the overlay repeatedly, which used to flash it
    /// shut before the user ever saw it.</param>
    public void Summon(long hideGraceMs = ShownDeactivationGraceMs)
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
        _hideGraceMs = hideGraceMs;

        // Raise the overlay above every normal window before showing it. A
        // programmatic summon (share/Open With handoff, tray) runs while another
        // app holds the foreground, and Windows denies SetForegroundWindow to
        // background processes — without this raise the freshly shown overlay
        // stays at its old Z position, invisible behind the user's windows, and
        // the summon looks like nothing happened. Topmost is safe here: the
        // overlay hides itself on the next deactivation, so it never lingers
        // above other apps.
        const uint SWP_NOMOVE_S = 0x0002, SWP_NOSIZE_S = 0x0001, SWP_NOACTIVATE = 0x0010;
        SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            SWP_NOMOVE_S | SWP_NOSIZE_S | SWP_NOACTIVATE);

        AppWindow.Show();
        ShowWindow(_hwnd, SW_SHOW);
        TakeForegroundInput();
        Common.Log.Info("overlay summon: window shown");

        // Now that the window is on screen, refresh the chrome and navigate
        // the WebView2 to the loaded model's WebUI.
        RefreshModelBadge();
        NavigateToModel();
        FocusComposerWhenReady();

        Common.Log.Info("overlay summon: done");
    }

    /// <summary>
    /// Asks Windows to make the overlay the foreground window so keystrokes
    /// land in the chat instead of the app the user was in. A plain
    /// <see cref="SetForegroundWindow"/> is denied while another process
    /// holds the foreground (programmatic summons run in the background), so
    /// borrow the foreground thread's input state via
    /// <see cref="AttachThreadInput"/> — the documented escape hatch that
    /// makes the raise succeed. Failures are harmless: the overlay is still
    /// visible (topmost) and one click focuses it, same as before.
    /// </summary>
    private void TakeForegroundInput()
    {
        if (GetForegroundWindow() == _hwnd) return; // already ours (hotkey path)
        try
        {
            var ourThread = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            if (foregroundThread != 0 && foregroundThread != ourThread)
            {
                AttachThreadInput(ourThread, foregroundThread, true);
                SetForegroundWindow(_hwnd);
                AttachThreadInput(ourThread, foregroundThread, false);
            }
            else
            {
                SetForegroundWindow(_hwnd);
            }
        }
        catch (Exception ex)
        {
            Common.Log.Debug($"overlay: foreground request failed ({ex.Message})");
        }
    }

    /// <summary>
    /// Focuses the WebUI's chat composer so the user can type immediately
    /// after the overlay opens — without this the page loads with the
    /// composer unfocused and a mouse click is needed first. Retries for a
    /// few seconds because the page (and its composer) may still be
    /// navigating; gives up quietly if the WebUI never shows a composer.
    /// </summary>
    private async void FocusComposerWhenReady()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var core = ModelWebUi.CoreWebView2;
                if (core is not null)
                {
                    var result = await core.ExecuteScriptAsync(
                        OverlayPromptInjection.BuildComposerFocus());
                    if (bool.TryParse(result, out var focused) && focused)
                    {
                        Common.Log.Debug("overlay webview: composer focused");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Common.Log.Debug($"overlay webview: composer focus not ready ({ex.Message})");
            }
            await Task.Delay(500);
        }
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
    /// Initializes the overlay's WebView2 NOW, at a calm point in app startup,
    /// instead of lazily at the first summon. Two reasons:
    ///
    /// <list type="bullet">
    /// <item>the first summon becomes instant (no init wait);</item>
    /// <item>the first initialization is the one that occasionally trips the
    /// x64-on-ARM64 emulation layer (an unobserved WinRT failure surfaced as
    /// a stowed exception — 0xC000027B from the SE code in xtajit64se — which
    /// fail-fasts the process and no handler can catch). It reproduced when a
    /// share delivery triggered the first summon while the OS share machinery
    /// was churning around the process. Prewarming moves that first
    /// initialization to startup, where it is awaited in a fully-observed
    /// context with nothing racing it; a failure here is caught, logged, and
    /// retried at the next summon instead of killing the app.</item>
    /// </list>
    /// </summary>
    public void PrewarmWebView()
    {
        if (ModelWebUi.CoreWebView2 is not null || _webViewInitStarted)
            return; // already initialized (or an attempt in flight)

        _webViewInitStarted = true;
        _ = ConfigureWebViewThenNavigateAsync();
    }

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
            else
            {
                Common.Log.Info("overlay webview: initialized (prewarm); navigation policy armed");
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
        // Dismiss on deactivation (clicking away), the same UX as the tray
        // flyout — except within the summon's grace window (see Summon):
        // focus churn around a programmatic summon (the share sheet dismissing)
        // must not flash the overlay shut.
        if (args.WindowActivationState == WindowActivationState.Deactivated &&
            Environment.TickCount64 - _lastShownMs > _hideGraceMs)
        {
            Common.Log.Debug("overlay hidden by deactivation (focus lost)");
            Hide();
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}