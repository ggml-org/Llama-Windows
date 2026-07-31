using System.Drawing;
using System.Runtime.InteropServices;
using H.NotifyIcon.Core;
using LlamaApp.Common;
using Microsoft.UI.Dispatching;

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
/// <para><b>Why the base <see cref="TrayIcon"/> and not
/// <c>TrayIconWithContextMenu</c>:</b> <c>Shell_NotifyIcon(NIM_ADD)</c> fails
/// while the notification area doesn't exist yet — e.g. when the app
/// auto-starts at logon before explorer.exe is up, or while explorer is
/// restarting. <c>TrayIconWithContextMenu.Create()</c> runs on its own
/// background thread (for the context-menu message loop), so that failure is
/// an exception thrown where no app code can catch it — it takes the whole
/// process down (<c>InvalidOperationException: TryCreate failed</c>, CLR crash
/// 0xe0434352). The base <see cref="TrayIcon.Create"/> runs <b>synchronously
/// on the calling thread</b>, so the same failure is caught and retried here
/// (<see cref="CreateTrayIconWithRetryAsync"/>) and can never crash the app.
/// The right-click menu — the only thing the subclass would have added — is
/// shown manually via the public <see cref="PopupMenu.Show"/> API
/// (<see cref="ShowContextMenu"/>). The message window lives on the UI thread,
/// pumped by the WinUI message loop — the same pattern the library's own
/// WPF/WinUI integrations use.</para>
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
    private readonly Icon _icon;
    private readonly TrayIcon _trayIcon;
    private readonly PopupMenu _contextMenu;
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

        _trayIcon = new TrayIcon("LlamaApp")
        {
            ToolTip = "LlamaApp",
            Icon = _icon.Handle,
        };
        _trayIcon.MessageWindow.SubscribeToMouseEventReceived(OnMouseEvent);
        _trayIcon.MessageWindow.SubscribeToTaskbarCreated(OnTaskbarCreated);

        // Shown manually on right-click (see ShowContextMenu).
        _contextMenu = BuildContextMenu();

        // Create the icon, retrying until the shell accepts it (see the class
        // remarks). Fire-and-forget: attempts are separated by awaits that
        // yield the UI thread, so a slow logon doesn't block startup, and the
        // rest of the app (flyout, overlay, hotkey) works without the icon
        // meanwhile.
        _ = CreateTrayIconWithRetryAsync("startup");
    }

    /// <summary>
    /// Calls the real <see cref="TrayIcon.Create"/> on the UI thread, retrying
    /// on failure. <c>Shell_NotifyIcon(NIM_ADD)</c> fails while the
    /// notification area doesn't exist yet (logon race, explorer restart), so
    /// the first attempts may fail — the exception is caught HERE (unlike with
    /// <c>TrayIconWithContextMenu</c>, whose background-thread creation made
    /// it an unhandleable process crash) and retried for ~60s before giving up
    /// gracefully: a machine without an interactive shell (kiosk, Server Core)
    /// just runs without a tray icon.
    /// </summary>
    private async Task CreateTrayIconWithRetryAsync(string reason)
    {
        // One loop at a time: a taskbar restart arriving while initial retries
        // are in flight just lets the in-flight loop's next attempt re-add.
        if (Interlocked.Exchange(ref _recreating, 1) == 1) return;
        try
        {
            for (var attempt = 1; attempt <= 60 && !_disposed; attempt++)
            {
                try
                {
                    // TryRemove is a no-op before the first successful create;
                    // afterwards it resets IsCreated (best-effort delete — the
                    // icon is usually already gone) so Create re-adds it. That
                    // makes this same loop serve both initial creation and the
                    // taskbar-restart re-add.
                    _trayIcon.TryRemove();
                    _trayIcon.Create();
                    Log.Info(attempt > 1
                        ? $"tray icon created ({reason}, after {attempt} attempts)"
                        : "tray icon created");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return; // shutting down — stop quietly
                }
                catch (Exception ex)
                {
                    if (attempt is 1 or 10 or 30)
                        Log.Warn(ex, $"tray icon creation failed ({reason}, attempt {attempt}); retrying");
                    await Task.Delay(1000);
                }
            }
            if (!_disposed)
                Log.Error($"tray icon could not be created ({reason}); running without a tray icon");
        }
        finally
        {
            Interlocked.Exchange(ref _recreating, 0);
        }
    }

    /// <summary>
    /// Re-adds the icon after explorer (re)created the taskbar — every tray
    /// icon is wiped at that point. The broadcast can arrive while the
    /// notification area is still initializing, so this goes through the same
    /// retrying path as initial creation.
    /// </summary>
    private void OnTaskbarCreated(object? sender, EventArgs e)
    {
        Log.Info("taskbar (re)created; re-adding tray icon");
        _ = CreateTrayIconWithRetryAsync("taskbar restart");
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
        // The message window lives on the UI thread, so this already runs
        // there — no marshaling needed.

        // Record the click location for every button so the Open entry works
        // regardless of which click preceded the context menu.
        CursorPoint = e.Point;

        switch (e.MouseEvent)
        {
            case MouseEvent.IconLeftMouseUp:
                // Left-click makes the flyout visible. If it was *just*
                // dismissed by this very click (clicking the icon deactivates
                // the open flyout, which auto-hides it), the grace period keeps
                // it closed instead of bouncing it back open.
                if (_window.WasJustHiddenByDeactivate)
                    return;
                _window.ShowAsFlyout(e.Point);
                break;

            case MouseEvent.IconRightMouseUp:
                ShowContextMenu();
                break;
        }
    }

    /// <summary>
    /// Shows the native context menu at the cursor — exactly what
    /// <c>TrayIconWithContextMenu</c> does internally. The owner window must
    /// be moved to the foreground first, otherwise the menu doesn't dismiss
    /// when the user clicks elsewhere. <see cref="PopupMenu.Show"/> blocks in
    /// a modal menu loop (<c>TPM_RETURNCMD</c>) until an item is chosen or the
    /// menu is dismissed; the chosen item's callback runs before it returns.
    /// </summary>
    private void ShowContextMenu()
    {
        if (!_trayIcon.IsCreated) return;
        var pos = CurrentCursorPoint();
        _ = SetForegroundWindow(_trayIcon.WindowHandle);
        _contextMenu.Show(_trayIcon.WindowHandle, pos.X, pos.Y);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

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
            _trayIcon.Dispose();
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
    /// Runs <paramref name="action"/> on the UI thread. Public entry points
    /// (toast-notification clicks, single-instance redirects) can arrive on
    /// any thread; marshaling keeps all WinUI window access on the UI thread.
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
        _trayIcon.Dispose();
        _icon.Dispose();
    }
}
