using LlamaApp.Common;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LlamaApp;

/// <summary>
/// Thin wrapper over the Windows App SDK toast-notification API
/// (<see cref="AppNotificationManager"/>). Used to surface background events —
/// a model finishing loading, a download failing — while the tray flyout is
/// hidden. Clicking a toast re-opens the flyout (see <see cref="Invoked"/>).
///
/// <para>All failures are swallowed (and logged): notifications are a nicety,
/// never a reason to crash — e.g. when the notification platform is
/// unavailable or registration is rejected.</para>
/// </summary>
internal static class Notifications
{
    private static bool _registered;

    /// <summary>
    /// Raised when the user clicks a toast. Fires on a COM callback thread —
    /// marshaling to the UI thread is the subscriber's job.
    /// </summary>
    public static event Action? Invoked;

    /// <summary>
    /// Registers the app with the notification platform and hooks the
    /// invoked callback. Call once at startup; pairs with
    /// <see cref="Unregister"/> on exit.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) => Invoked?.Invoke();
            AppNotificationManager.Default.Register();
            _registered = true;
            Log.Info("toast notifications registered");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast notification registration failed; toasts disabled");
        }
    }

    /// <summary>
    /// Shows a simple two-line toast (title + body). No-op when registration
    /// failed at startup.
    /// </summary>
    public static void Show(string title, string body)
    {
        if (!_registered) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast show failed");
        }
    }

    /// <summary>Unregisters the app's notification activator; call on exit.</summary>
    public static void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) { Log.Warn(ex, "toast unregister failed"); }
    }
}
