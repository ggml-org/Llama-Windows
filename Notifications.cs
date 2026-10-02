using LlamaApp.Common;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LlamaApp;

/// <summary>
/// A single interactive button on a toast: a label plus its activation
/// arguments (<c>key=value</c> pairs the <see cref="Notifications.Invoked"/>
/// handler receives when the button is clicked).
/// </summary>
/// <example>
/// <c>new ToastAction("Retry", ("action", "retry"), ("id", serverId))</c>
/// </example>
internal sealed record ToastAction(string Label, params (string Key, string Value)[] Arguments);

/// <summary>
/// Thin wrapper over the Windows App SDK toast-notification API
/// (<see cref="AppNotificationManager"/>). Used to surface background events —
/// a model finishing loading, a download failing — while the tray flyout is
/// hidden. Clicking a toast re-opens the flyout; clicking an action button
/// routes its arguments to <see cref="Invoked"/>, which the app maps to a
/// specific response (retry a failed download, cancel a running one, open the
/// chat overlay — see <c>App.HandleToastActivation</c>).
///
/// <para>Progress toasts are keyed by a caller-chosen <c>tag</c>: showing
/// again with the same tag replaces the toast in place, and
/// <see cref="Close(string)"/> dismisses it — that's how a model download
/// streams its progress without spamming Action Center with one toast per
/// percent.</para>
///
/// <para>All failures are swallowed (and logged): notifications are a nicety,
/// never a reason to crash — e.g. when the notification platform is
/// unavailable or registration is rejected.</para>
/// </summary>
internal static class Notifications
{
    private static bool _registered;

    /// <summary>
    /// Raised when the user activates a toast — body click or action button.
    /// Carries the toast's activation arguments (empty for a body click).
    /// Fires on a COM callback thread — marshaling to the UI thread is the
    /// subscriber's job.
    /// </summary>
    public static event Action<IReadOnlyDictionary<string, string>>? Invoked;

    /// <summary>
    /// Registers the app with the notification platform and hooks the
    /// invoked callback. Call once at startup; pairs with
    /// <see cref="Unregister"/> on exit.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            // Copy the argument map off the COM thread before dispatching —
            // the event args (and its runtime-provided dictionary) are not
            // safe to touch from another thread afterwards.
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
                Invoked?.Invoke(new Dictionary<string, string>(e.Arguments));
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
    /// Shows a simple two-line toast (title + body). Activating it raises
    /// <see cref="Invoked"/> with no arguments. No-op when registration
    /// failed at startup.
    /// </summary>
    public static void Show(string title, string body)
        => ShowToast(title, body, tag: null, actions: null, progressBar: null);

    /// <summary>
    /// Shows a two-line toast with action buttons. Activating a button raises
    /// <see cref="Invoked"/> with that button's arguments; a body click raises
    /// it with none.
    /// </summary>
    public static void Show(string title, string body, params ToastAction[] actions)
        => ShowToast(title, body, tag: null, actions, progressBar: null);

    /// <summary>
    /// Shows (or replaces, on a repeated <paramref name="tag"/>) a progress
    /// toast: title, a determinate progress bar, and an optional status line
    /// (e.g. "42% · 12.3 MB/s"). Pass <paramref name="fraction"/> null for an
    /// indeterminate-looking bar (value stuck at 0 with a status caption).
    /// </summary>
    public static void ShowProgress(
        string tag, string title, string status, double? fraction,
        params ToastAction[] actions)
        => ShowToast(title, status, tag, actions,
            progressBar: new AppNotificationProgressBar()
                .SetValue(fraction ?? 0)
                .SetStatus(status));

    /// <summary>
    /// Dismisses the progress toast with this tag (e.g. when the download
    /// completes, fails, or the flyout is opened and the row takes over the
    /// story). Best-effort, fire-and-forget.
    /// </summary>
    public static void Close(string tag)
    {
        if (!_registered) return;
        try
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(tag);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast close failed");
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

    /// <summary>
    /// Builds and shows one toast. A <paramref name="tag"/> makes the toast
    /// replaceable in place (progress updates); <paramref name="actions"/>
    /// become its buttons; <paramref name="progressBar"/> (when present)
    /// replaces the second text line.
    /// </summary>
    private static void ShowToast(
        string title, string? body, string? tag, ToastAction[]? actions,
        AppNotificationProgressBar? progressBar)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder().AddText(title);
            if (progressBar is not null)
                builder = builder.AddProgressBar(progressBar);
            else if (!string.IsNullOrEmpty(body))
                builder = builder.AddText(body);

            if (actions is { Length: > 0 })
                foreach (var action in actions)
                {
                    var button = new AppNotificationButton(action.Label);
                    foreach (var (key, value) in action.Arguments)
                        button.AddArgument(key, value);
                    builder = builder.AddButton(button);
                }

            if (tag is not null)
                builder = builder.SetTag(tag).SetGroup(ProgressGroup);

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast show failed");
        }
    }

    /// <summary>Group shared by every replaceable progress toast.</summary>
    private const string ProgressGroup = "Llama";
}
