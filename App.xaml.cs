namespace LlamaApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private TrayIconManager? _trayIcon;
        
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

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

            // Ensure a llama.cpp binary is present, installing it on demand via
            // install.ps1 when none is found. Fire-and-forget: it runs on the UI
            // thread so LlamaManager.StateChanged (and thus the footer version
            // line) updates land on the UI thread. The flyout footer shows the
            // resolved version (or "not installed" / the install state).
            _ = Llama.LlamaManager.Shared.EnsureReadyAsync();
        }
    }
}