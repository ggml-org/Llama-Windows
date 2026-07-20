namespace LlamaApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private TrayIconManager? _trayIcon;
        private Views.OverlayWindow? _overlay;
        private GlobalHotkey? _hotkey;
        
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Initialize the global logger first, before anything that can
            // fail: a debug build logs at Debug level, a release build at Info.
            // The log lives at %LOCALAPPDATA%\LlamaApp\logs\LlamaApp-YYYYMMDD.log
            // (rolled daily, old files swept to a 7-day retention).
            Common.Log.Initialize(level:
#if DEBUG
                Common.LogLevel.Debug
#else
                Common.LogLevel.Info
#endif
            );
            Common.Log.Info("LlamaApp starting");

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
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
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

            // Ensure a llama.cpp server is reachable at localhost:2276: adopt an
            // already-running one, launch one from a found binary, or download the
            // binary via install.ps1 then launch. Fire-and-forget; once reachable the
            // MainWindow fetches the Available model list via GET /models.
            Llama.LlamaManager.Shared.CacheDirectory = Settings.Current.CacheDirectory;
            Common.Log.Info($"Cache directory: {Settings.Current.CacheDirectory}");
            _ = Llama.LlamaManager.Shared.EnsureLlamaOrDownloadAsync();

            // Spotlight-style prompt overlay, summoned by a global Alt+Space
            // hotkey. Created lazily on first press and reused thereafter; the
            // hotkey is registered for the app's lifetime only, so it stops
            // working the moment LlamaApp exits.
            _overlay = new Views.OverlayWindow();
            _hotkey = new GlobalHotkey();
            _hotkey.Register(() => _dispatcher.TryEnqueue(() => _overlay.Summon()));
        }
    }
}