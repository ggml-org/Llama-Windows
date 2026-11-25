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
/// <para>Progress toasts are keyed by a caller-chosen <c>tag</c>. The first
/// report shows a data-bound toast; later reports use silent progress updates,
/// so a download does not re-display a popup or reorder Notification Center on
/// every tick. <see cref="Close(string)"/> hides it transiently, while
/// <see cref="Close(string, bool)"/> can seal it on a terminal path.</para>
///
/// <para>All failures are swallowed (and logged): notifications are a nicety,
/// never a reason to crash — e.g. when the notification platform is
/// unavailable or registration is rejected.</para>
/// </summary>
internal static class Notifications
{
    private static bool _registered;
    private static readonly object ProgressGate = new();
    private static readonly Dictionary<string, ProgressToastState> ProgressToasts = new(StringComparer.Ordinal);

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
        => ShowToast(title, body, actions: null);

    /// <summary>
    /// Shows a two-line toast with action buttons. Activating a button raises
    /// <see cref="Invoked"/> with that button's arguments; a body click raises
    /// it with none.
    /// </summary>
    public static void Show(string title, string body, params ToastAction[] actions)
        => ShowToast(title, body, actions);

    /// <summary>
    /// Starts a new download attempt. This resets a prior dismissal or terminal
    /// seal while keeping late callbacks from the old session harmless.
    /// </summary>
    public static void BeginProgress(string tag)
    {
        lock (ProgressGate)
            ProgressToasts[tag] = new ProgressToastState();
    }

    /// <summary>
    /// Shows a progress toast on the first report, then updates its bound
    /// values silently on subsequent reports. Pass <paramref name="fraction"/>
    /// null for an indeterminate-looking bar (value stuck at 0 with a status
    /// caption). A dismissed or terminally closed toast is never re-shown by a
    /// later report from the same session.
    /// </summary>
    public static void ShowProgress(
        string tag, string title, string status, double? fraction,
        params ToastAction[] actions)
    {
        if (!_registered) return;
        try
        {
            ProgressToastState state;
            AppNotificationProgressData data;
            uint generation;
            lock (ProgressGate)
            {
                if (!ProgressToasts.TryGetValue(tag, out state!))
                {
                    state = new ProgressToastState();
                    ProgressToasts.Add(tag, state);
                }

                var action = state.NextAction();
                if (action == ProgressToastAction.Ignore) return;

                generation = state.Generation;
                data = CreateProgressData(state, status, fraction);
                if (action == ProgressToastAction.Show)
                {
                    // Only this first Show is allowed to display a popup. The
                    // buttons live in this payload and survive later updates.
                    var notification = BuildProgressToast(tag, title, actions, data);
                    AppNotificationManager.Default.Show(notification);
                    state.MarkShown();
                    return;
                }
            }

            // Do not replace the toast on ticks: replacement re-displays and
            // reorders it. Sequence numbers let the platform reject stale data.
            _ = UpdateProgressAsync(tag, state, generation, data);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast progress show/update failed");
        }
    }

    /// <summary>
    /// Hides a progress toast temporarily, such as when the flyout takes over
    /// the progress display. A later report may show it again unless the user
    /// dismissed it. Removal is best-effort and fire-and-forget.
    /// </summary>
    public static void Close(string tag) => Close(tag, terminal: false);

    /// <summary>
    /// Hides a progress toast and, when <paramref name="terminal"/> is true,
    /// seals its session against queued callbacks. Grouped toasts must be
    /// removed by both tag and group; tag-only removal leaves them behind.
    /// </summary>
    public static void Close(string tag, bool terminal)
    {
        try
        {
            lock (ProgressGate)
            {
                if (!ProgressToasts.TryGetValue(tag, out var state) && terminal)
                {
                    // Seal even if no report ever showed a toast: a queued
                    // first callback must not create a ghost after completion.
                    state = new ProgressToastState();
                    ProgressToasts.Add(tag, state);
                }
                state?.Close(terminal);
            }

            if (_registered) _ = RemoveProgressAsync(tag);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast close failed");
        }
    }

    private static AppNotificationProgressData CreateProgressData(
        ProgressToastState state, string status, double? fraction)
        => new(state.NextSequence())
        {
            Value = fraction ?? 0,
            ValueStringOverride = fraction is { } value
                ? (int)Math.Round(value * 100) + "%"
                : "",
            Status = status,
        };

    private static AppNotification BuildProgressToast(
        string tag, string title, ToastAction[] actions, AppNotificationProgressData data)
    {
        var builder = new AppNotificationBuilder()
            .AddText(title)
            .AddProgressBar(new AppNotificationProgressBar()
                .BindValue()
                .BindValueStringOverride()
                .BindStatus());

        foreach (var action in actions)
        {
            var button = new AppNotificationButton(action.Label);
            foreach (var (key, value) in action.Arguments)
                button.AddArgument(key, value);
            builder = builder.AddButton(button);
        }

        var notification = builder.SetTag(tag).SetGroup(ProgressGroup).BuildNotification();
        notification.Progress = data;
        return notification;
    }

    private static async Task UpdateProgressAsync(
        string tag, ProgressToastState state, uint generation, AppNotificationProgressData data)
    {
        try
        {
            var result = await AppNotificationManager.Default.UpdateAsync(data, tag, ProgressGroup);
            if (result == AppNotificationProgressResult.Succeeded) return;

            lock (ProgressGate)
            {
                // A transient close followed by a later display must not let
                // an old async result suppress the new display or session.
                if (ProgressToasts.TryGetValue(tag, out var current) &&
                    ReferenceEquals(current, state) && current.Generation == generation)
                    state.Suppress();
            }

            if (result == AppNotificationProgressResult.AppNotificationNotFound)
                Log.Debug("progress toast was dismissed; stopping updates");
            else
                Log.Warn($"progress toast updates returned {result}; stopping updates");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast progress update failed");
        }
    }

    private static async Task RemoveProgressAsync(string tag)
    {
        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, ProgressGroup);
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
        lock (ProgressGate) ProgressToasts.Clear();
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) { Log.Warn(ex, "toast unregister failed"); }
    }

    /// <summary>Builds and shows one plain toast with optional action buttons.</summary>
    private static void ShowToast(string title, string? body, ToastAction[]? actions)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder().AddText(title);
            if (!string.IsNullOrEmpty(body))
                builder = builder.AddText(body);

            if (actions is { Length: > 0 })
                foreach (var action in actions)
                {
                    var button = new AppNotificationButton(action.Label);
                    foreach (var (key, value) in action.Arguments)
                        button.AddArgument(key, value);
                    builder = builder.AddButton(button);
                }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "toast show failed");
        }
    }

    /// <summary>Group shared by every data-bound progress toast.</summary>
    private const string ProgressGroup = "Llama";
}
