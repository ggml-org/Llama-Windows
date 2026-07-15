using System.Drawing;
using System.IO;
using H.NotifyIcon.Core;
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
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly MainWindow _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly TrayIconWithContextMenu _trayIcon;
    private readonly Icon _icon;
    private bool _disposed;

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

        _trayIcon = new TrayIconWithContextMenu("LlamaApp")
        {
            ToolTip = "LlamaApp",
            Icon = _icon.Handle,
            // Shown by TrayIconWithContextMenu automatically on right-click.
            ContextMenu = BuildContextMenu(),
        };

        _trayIcon.Create();
        // The base class only auto-shows the context menu on right-click; we
        // additionally show the flyout on a left-click.
        _trayIcon.MessageWindow.SubscribeToMouseEventReceived(OnMouseEvent);
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
    /// Shuts the app down: removes the tray icon (so it doesn't linger in the
    /// notification area), then closes the window and exits.
    /// </summary>
    public void RequestExit()
    {
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
        catch
        {
            // Best-effort cleanup during shutdown.
        }

        _window.AllowClose = true;
        try { _window.Close(); } catch { /* best-effort */ }
        Environment.Exit(0);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Tray-icon callbacks
    /// arrive on the message window's thread; marshalling keeps all WinUI
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
        _trayIcon.Dispose();
        _icon.Dispose();
    }
}
