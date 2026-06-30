using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;

namespace LlamaApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        
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
            _window = new MainWindow();
            _window.Title = "LlamaApp";
            _window.AppWindow.SetTitleBarIcon(@"Assets\llama.svg");
            _window.AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            _window.Activate();
        }
    }
}