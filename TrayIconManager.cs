using System.Drawing;
using System.Runtime.InteropServices;
using H.NotifyIcon.Core;
using LlamaApp.Common;
using Microsoft.UI.Dispatching;
using WinRT.Interop;

namespace LlamaApp;

/// <summary>
/// Owns the system-tray (notification area) icon for LlamaApp.
///
/// <para><b>Left-click</b> makes the <see cref="MainWindow"/> flyout visible —
/// a borderless Mica panel pinned to the bottom-right of the screen (just above
/// the taskbar, next to the tray), mirroring the macOS menu-bar app's popup on
/// Windows 11. The flyout auto-hides when it loses focus (clicking elsewhere),
/// and a short deactivation grace period keeps a left-click that *dismissed* it
/// from bouncing it straight back open.</para>
///
/// <para><b>Right-click</b> shows a native Win32 context menu with
/// <c>Open</c> and <c>Exit</c> — the standard Windows tray affordance.</para>
///
/// This uses the low-level <c>H.NotifyIcon.Core</c> API directly (rather than
/// the optional <c>H.NotifyIcon.WinUI</c> XAML control) so we only depend on
/// the already-referenced core package.
///
/// <para><b>Shell-readiness gating:</b> <c>Shell_NotifyIcon(NIM_ADD)</c> fails
/// while the taskbar doesn't exist yet — e.g. when the app auto-starts at
/// logon before explorer.exe is up, or while explorer is restarting.
/// H.NotifyIcon turns that failure into an exception thrown on its own
/// background message-loop thread (<c>TrayIconWithContextMenu.Create</c>),
/// which is unhandleable from the outside and takes the whole process down
/// (<c>InvalidOperationException: TryCreate failed</c>, CLR crash
/// 0xe0434352). So the icon is created only once the notification area
/// <i>provably</i> accepts icons — verified by registering and immediately
/// removing a throwaway probe icon through the very same API
/// (<see cref="WaitForShellAndCreateAsync"/>).</para>
///
/// <para><b>Explorer restarts</b> wipe every tray icon; the taskbar then
/// broadcasts <c>TaskbarCreated</c> and each app must re-add its icon.
/// H.NotifyIcon 2.4.1 raises the event but leaves the re-adding to the app
/// (<see cref="OnTaskbarCreated"/>).</para>
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly MainWindow _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly nint _windowHandle;
    private readonly Icon _icon;

    // Created asynchronously once the shell is ready — null until then.
    private TrayIconWithContextMenu? _trayIcon;
    private bool _disposed;
    private int _recreating;

    public TrayIconManager(MainWindow window)
    {
        _window = window;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "TrayIconManager must be constructed on the UI thread.");

        // The Assets folder is copied next to the executable, so resolve the
        // icon relative to the app's base directory rather than the CWD.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "llama.ico");
        _icon = new Icon(iconPath);

        // The probe icon is registered against the main window's handle.
        _windowHandle = WindowNative.GetWindowHandle(window);

        // Create the icon only once the shell provably accepts icons (see the
        // class remarks). Fire-and-forget: the rest of the app (flyout,
        // overlay, hotkey) works without the tray icon meanwhile.
        _ = Task.Run(WaitForShellAndCreateAsync);
    }

    /// <summary>
    /// Waits (on a background thread) until the Windows notification area
    /// actually accepts icons, then creates the tray icon on the UI thread.
    /// At logon the taskbar usually appears within seconds; a machine without
    /// an interactive shell (kiosk, Server Core) gives up after ~60s and the
    /// app simply runs without a tray icon instead of crashing.
    /// </summary>
    private async Task WaitForShellAndCreateAsync()
    {
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            if (_disposed) return;

            if (TaskbarExists() && ProbeNotifyIcon())
            {
                if (attempt > 1)
                    Log.Info($"shell notification area ready after {attempt}s; creating tray icon");
                _dispatcher.TryEnqueue(CreateTrayIcon);
                return;
            }

            if (attempt == 1)
                Log.Warn("shell notification area not ready yet (app likely started at logon before the taskbar); waiting");
            await Task.Delay(1000);
        }

        Log.Error("shell notification area never became ready; running without a tray icon");
    }

    /// <summary>
    /// Creates the tray icon on the UI thread — called only once the shell has
    /// provably accepted a probe icon, so <c>TrayIcon.Create</c> can't fail
    /// (and take the process down on the library's thread).
    /// </summary>
    private void CreateTrayIcon()
    {
        if (_disposed || _trayIcon is not null) return;

        var trayIcon = new TrayIconWithContextMenu("LlamaApp")
        {
            ToolTip = "LlamaApp",
            Icon = _icon.Handle,
            // Shown by TrayIconWithContextMenu automatically on right-click.
            ContextMenu = BuildContextMenu(),
        };

        // Explorer restarts wipe every tray icon; re-add ours when the taskbar
        // broadcasts "TaskbarCreated".
        trayIcon.MessageWindow.SubscribeToTaskbarCreated(OnTaskbarCreated);

        try
        {
            trayIcon.Create();
        }
        catch (Exception ex)
        {
            // The shell died between our probe and creation (e.g. an explorer
            // crash in that exact instant) — don't let it take the app down:
            // go back to waiting for the shell and try again.
            Log.Warn(ex, "tray icon creation failed despite a ready shell; retrying");
            try { trayIcon.Dispose(); } catch { /* best-effort */ }
            _ = Task.Run(WaitForShellAndCreateAsync);
            return;
        }

        Log.Info("tray icon created");
        // The base class only auto-shows the context menu on right-click; we
        // additionally show the flyout on a left-click.
        trayIcon.MessageWindow.SubscribeToMouseEventReceived(OnMouseEvent);

        _trayIcon = trayIcon;
    }

    /// <summary>
    /// Re-adds the icon after explorer (re)created the taskbar — every tray
    /// icon is wiped at that point. Raised on the message-window thread; the
    /// work is moved to a background task so a still-initializing shell (the
    /// broadcast can arrive slightly before the notification area accepts
    /// icons) doesn't block the context-menu message loop while retrying.
    /// </summary>
    private void OnTaskbarCreated(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _recreating, 1) == 1)
            return;

        Log.Info("taskbar (re)created; re-adding tray icon");
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= 20; attempt++)
                {
                    if (_disposed) return;
                    var trayIcon = _trayIcon;
                    if (trayIcon is null) return;
                    try
                    {
                        // TryRemove resets IsCreated (best-effort delete — the
                        // icon is usually already gone); Create then re-adds.
                        // With the message-loop thread already running, this
                        // goes straight to TrayIcon.Create on the current
                        // thread — any failure is catchable HERE, unlike the
                        // initial creation.
                        trayIcon.TryRemove();
                        trayIcon.Create();
                        Log.Info("tray icon re-added after taskbar restart");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, $"tray icon re-add attempt {attempt} failed");
                        await Task.Delay(500);
                    }
                }
                Log.Error("giving up re-adding the tray icon after taskbar restart");
            }
            finally
            {
                Interlocked.Exchange(ref _recreating, 0);
            }
        });
    }

    /// <summary>
    /// The minimal native context menu shown on right-click — the standard
    /// Windows tray affordance: <c>Open</c> reveals the flyout, <c>Exit</c>
    /// quits the app.
    /// </summary>
    private PopupMenu BuildContextMenu()
    {
        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem("Open", (_, _) => Enqueue(() => _window.ShowAsFlyout(CursorPoint))));
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem("Exit", (_, _) => Enqueue(RequestExit)));
        return menu;
    }

    /// <summary>
    /// The most recent tray-icon click point, captured from the mouse event so
    /// the <c>Open</c> context-menu item (whose callback receives no position)
    /// can still hand a reference point to <see cref="MainWindow.ShowAsFlyout"/>
    /// for monitor selection.
    /// </summary>
    private Point CursorPoint { get; set; }

    private void OnMouseEvent(object? sender, MessageWindow.MouseEventReceivedEventArgs e)
    {
        // Record the click location for every button so the Open entry works
        // regardless of which click preceded the context menu.
        CursorPoint = e.Point;

        if (e.MouseEvent == MouseEvent.IconLeftMouseUp)
        {
            // Left-click makes the flyout visible. If it was *just* dismissed
            // by this very click (clicking the icon deactivates the open
            // flyout, which auto-hides it), the grace period keeps it closed
            // instead of bouncing it back open.
            Enqueue(() =>
            {
                if (_window.WasJustHiddenByDeactivate)
                    return;
                _window.ShowAsFlyout(e.Point);
            });
        }
    }

    /// <summary>
    /// Shows the flyout pinned to the monitor the cursor is currently on — for
    /// activations that carry no position of their own (toast-notification
    /// clicks, single-instance redirects).
    /// </summary>
    public void ShowFlyout()
    {
        Enqueue(() => _window.ShowAsFlyout(CurrentCursorPoint()));
    }

    private static Point CurrentCursorPoint()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    // ---- Shell-readiness probe ----

    /// <summary>
    /// The taskbar window exists. A cheap first gate before the real probe —
    /// its absence means the shell definitely isn't ready.
    /// </summary>
    private static bool TaskbarExists() => FindWindowW("Shell_TrayWnd", null) != 0;

    /// <summary>
    /// Registers a throwaway icon through <c>Shell_NotifyIcon(NIM_ADD)</c> —
    /// the exact call <c>TrayIcon.Create</c> will make — and immediately
    /// removes it. Only a <see langword="true"/> result proves the
    /// notification area accepts icons right now, which is what makes the
    /// library's subsequent <c>Create</c> (whose failure is an unhandleable
    /// crash on its own thread) safe to call. The probe entry is scoped to
    /// the main window's handle with its own id, so it can't collide with
    /// the real icon (which lives on the library's message window).
    /// </summary>
    private bool ProbeNotifyIcon()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _windowHandle,
            uID = ProbeIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            // Never delivered: the probe is deleted before anyone can
            // interact with it, and the main window ignores WM_APP+0 anyway.
            uCallbackMessage = WM_APP,
            hIcon = _icon.Handle,
            szTip = "LlamaApp",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            Log.Debug($"notification-area probe failed (win32 {Marshal.GetLastWin32Error()})");
            return false;
        }
        _ = Shell_NotifyIconW(NIM_DELETE, ref data);
        return true;
    }

    // Arbitrary id for the probe icon — uIDs are scoped per-hWnd, and the
    // real icon lives on the library's message window, so any value works.
    private const uint ProbeIconId = 0x4C4C4D41; // "LLMA"

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint WM_APP = 0x8000;

    // The Vista+ layout (976 bytes) — accepted by every supported Windows.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Shuts the app down: removes the tray icon (so it doesn't linger in the
    /// notification area), then closes the window, and exits.
    /// </summary>
    public void RequestExit()
    {
        Log.Info("app exit requested");

        // Unregister the toast-notification activator before going down.
        Notifications.Unregister();
        // Stop the llama server first so it doesn't outlive the app (and leave
        // the port bound). StopServer only kills a MANAGED server — one an app
        // instance started, tracked via the .llama.pid file (crash-safe); a
        // server the user started manually is left running. Best-effort:
        // Environment.Exit below guarantees the app itself terminates regardless.
        try { Llama.LlamaManager.Shared.StopServer(); }
        catch (Exception ex) { Log.Warn(ex, "best-effort server stop on exit failed"); }

        // Removing the tray icon first avoids a stray icon lingering in the
        // notification area after the process is gone. Letting the window close
        // normally (AllowClose) bypasses its "hide instead of close" guard;
        // Environment.Exit guarantees the process terminates even though the
        // tray message window is still alive.
        try
        {
            _trayIcon?.Dispose();
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            // Best-effort cleanup during shutdown.
            Log.Warn(ex, "best-effort tray icon dispose on exit failed");
        }

        _window.AllowClose = true;
        try { _window.Close(); } catch { /* best-effort */ }
        Environment.Exit(0);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Tray-icon callbacks
    /// arrive on the message window's thread; marshaling keeps all WinUI
    /// window access on the UI thread.
    /// </summary>
    private void Enqueue(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon?.Dispose();
        _icon.Dispose();
    }
}
