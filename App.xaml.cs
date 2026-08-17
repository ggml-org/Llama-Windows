namespace LlamaApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private TrayIconManager? _trayIcon;
        private OverlayWindow? _overlay;
        private GlobalHotkey? _hotkey;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Move the pre-rename data folder (%LOCALAPPDATA%\LlamaApp) to its
            // new home (%LOCALAPPDATA%\Llama) before logging or settings touch it.
            Common.AppData.MigrateLegacyFolder();

            // Initialize the global logger first, before anything that can
            // fail: a debug build logs at Debug level, a release build at Info.
            // The log lives at %LOCALAPPDATA%\Llama\logs\Llama-YYYYMMDD.log
            // (rolled daily, old files swept to a 7-day retention).
            Common.Log.Initialize(level:
#if DEBUG
                Common.LogLevel.Debug
#else
                Common.LogLevel.Info
#endif
            );
            Common.Log.Info("Llama starting");

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                          ?? throw new InvalidOperationException("App must initialize on the UI thread.");

            InitializeComponent();
        }

        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = null!;

        /// <summary>
        /// Invoked when the end user launches the application normally.  Other entry points
        /// will be used, such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Single-instance: a second launch redirects its activation to the
            // already-running instance (which re-opens the flyout — see the
            // Activated handler below) and exits, instead of creating a
            // duplicate tray icon and a second /models poller fighting over
            // the same server port.
            var instance = Microsoft.Windows.AppLifecycle.AppInstance
                .FindOrRegisterForKey("Llama");
            if (!instance.IsCurrent)
            {
                Common.Log.Info("another instance is already running; redirecting activation and exiting");
                try
                {
                    await instance.RedirectActivationToAsync(
                        instance.GetActivatedEventArgs());
                }
                catch (Exception ex)
                {
                    Common.Log.Warn(ex, "activation redirect failed");
                }
                Environment.Exit(0);
                return;
            }

            // The startup shortcut was renamed with the app ("LlamaApp.lnk" →
            // "Llama.lnk") — sweep any leftover from a pre-rename install.
            StartupHelper.RemoveLegacyShortcut();

            // Create the llama-server manager singleton with the configured
            // port BEFORE anything else can touch LlamaManager.Shared — the
            // MainWindow constructor below already subscribes to its events.
            // The port is baked into the manager at construction, so a value
            // changed in Settings only takes effect on the next launch.
            var serverPort = Settings.Current.ServerPort;
            if (serverPort is < 1 or > 65535)
            {
                Common.Log.Warn($"configured server port {serverPort} is out of range; falling back to {Llama.LlamaManager.DefaultServerPort}");
                serverPort = Llama.LlamaManager.DefaultServerPort;
            }
            Llama.LlamaManager.Initialize(serverPort);

            // The app is tray-only: the main window is created but never shown
            // on launch. It is revealed on demand as a flyout anchored to the
            // system-tray icon (see TrayIconManager / MainWindow.ShowAsFlyout),
            // mirroring the macOS menu-bar app on Windows 11.
            _window = new MainWindow();

            // The footer's Quit button asks the tray manager to tear the app
            // down (dispose the icon, close the window, exit).
            _window.ExitRequested += () => _trayIcon?.RequestExit();

            // Add the system-tray icon with its right-click context menu.
            _trayIcon = new TrayIconManager(_window);

            // Toast notifications for background events (model loaded, download
            // failed) while the flyout is hidden. Clicking a toast re-opens the
            // flyout; so does a redirected second-launch activation.
            Notifications.Initialize();
            Notifications.Invoked += () => _dispatcher.TryEnqueue(() => _trayIcon?.ShowFlyout());
            instance.Activated += (_, _) => _dispatcher.TryEnqueue(() => _trayIcon?.ShowFlyout());

            // Ensure a llama.cpp server is reachable on the configured port:
            // adopt an already-running one, launch one from a found binary, or
            // download the binary via install.ps1 then launch. Fire-and-forget;
            // once reachable the MainWindow fetches the Available model list via
            // GET /models.
            Llama.LlamaManager.Shared.CacheDirectory = Settings.Current.CacheDirectory;
            Llama.LlamaManager.Shared.HuggingFaceToken = Settings.Current.HuggingFaceToken;
            Llama.LlamaManager.Shared.IdleUnloadSeconds = Settings.Current.IdleUnloadSeconds;
            Common.Log.Info($"cache directory: {Settings.Current.CacheDirectory}");
            // Presence only — never log the token itself.
            Common.Log.Info($"HF token: {(string.IsNullOrWhiteSpace(Settings.Current.HuggingFaceToken) ? "not set" : "configured")}");
            _ = Llama.LlamaManager.Shared.EnsureLlamaOrDownloadAsync();

            // Spotlight-style prompt overlay, summoned by a global Alt+Space
            // hotkey. Created lazily on first press and reused thereafter; the
            // hotkey is registered for the app's lifetime only, so it stops
            // working the moment Llama exits.
            _overlay = new OverlayWindow();
            _hotkey = new GlobalHotkey();
            _hotkey.Register(() => _dispatcher.TryEnqueue(() => _overlay.Summon()));

            // The hotkey is hard-coded and otherwise undiscoverable — say so
            // when it can't work instead of the shortcut silently doing
            // nothing (RegisterHotKey fails when another app owns Alt+Space).
            if (!_hotkey.IsRegistered)
            {
                Notifications.Show("Alt+Space unavailable",
                    "Another app is using the Alt+Space shortcut, so the chat overlay hotkey is off.");
            }

            // One-time first-run hint: the app is tray-only (no window appears
            // on launch) and the overlay hotkey is undiscoverable — tell the
            // user both, exactly once.
            if (!Settings.Current.TrayHintShown)
            {
                Settings.Current.TrayHintShown = true;
                Settings.Current.Save();
                Notifications.Show("Llama is running",
                    "Find it in the system tray — and press Alt+Space anytime to chat with a loaded model.");
            }
        }
    }
}