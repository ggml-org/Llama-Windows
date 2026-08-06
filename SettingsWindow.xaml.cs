using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace LlamaApp
{
    /// <summary>
    /// A small centered settings window (separate from the tray flyout) styled
    /// after the Windows 11 Settings app: a left NavigationView with three
    /// pages — General (launch at startup, local models cache), Identity
    /// (Hugging Face token) and Llama (server port). Saves to
    /// <see cref="Settings"/> on Save; discards on Cancel.
    /// </summary>
    public sealed partial class SettingsWindow : Window
    {
        // Settings window size in DIPs (the units XAML layout uses), centered on
        // the cursor's monitor. AppWindow sizes/positions are in PHYSICAL pixels,
        // so the DIPs are scaled by that monitor's DPI before Resize/Move — the
        // window shows the same amount of content on every screen, whatever the
        // display scaling. Clamped to the work area so small screens still fit.
        private const int WindowWidthDips = 960;
        private const int WindowHeightDips = 560;

        public SettingsWindow()
        {
            InitializeComponent();
            Title = "Settings";
            Configure();
            SizeAndCenterOnScreen();
            LoadCurrent();

            // Extend Mica/content into the titlebar area and register our
            // AppTitleBar element as the drag region. The system caption
            // buttons (close) stay on the right; this is what drops the
            // default white titlebar that clashes with the Mica backdrop.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }

        private void Configure()
        {
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            // Don't appear in Alt-Tab / taskbar switcher — it's a child dialog
            // of the tray app, not a standalone top-level window.
            AppWindow.IsShownInSwitchers = false;

            // Keep the border + titlebar (caption buttons) but let the Mica
            // backdrop fill the titlebar area (ExtendsContentIntoTitleBar set
            // in the ctor) — this is what drops the default white titlebar.
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
        }

        /// <summary>
        /// Sizes the window to the fixed DIP design size — scaled by the cursor
        /// monitor's DPI, clamped to its work area — then centers it there: the
        /// natural spot for a dialog spawned from a tray-only app (no owning
        /// window to center on).
        /// </summary>
        private void SizeAndCenterOnScreen()
        {
            var cursor = GetCursorPos(out var pt) ? pt : new POINT();
            var hmon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hmon, ref mi);

            int workWidth = mi.rcWork.Right - mi.rcWork.Left;
            int workHeight = mi.rcWork.Bottom - mi.rcWork.Top;

            // DIP size → physical pixels at this monitor's DPI (default 96 =
            // 100% scaling if the query fails), clamped to the work area with
            // a small margin so very small screens still fit the window.
            uint dpiX = 96, dpiY = 96;
            GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            int width = Math.Min((int)Math.Round(WindowWidthDips * dpiX / 96.0), (int)(workWidth * 0.92));
            int height = Math.Min((int)Math.Round(WindowHeightDips * dpiY / 96.0), (int)(workHeight * 0.92));

            AppWindow.Resize(new SizeInt32(width, height));
            int x = mi.rcWork.Left + (workWidth - width) / 2;
            int y = mi.rcWork.Top + (workHeight - height) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }

        private void LoadCurrent()
        {
            var s = Settings.Current;
            TokenBox.Password = s.HuggingFaceToken ?? "";
            CacheBox.Text = s.CacheDirectory ?? "";
            PortBox.Value = s.ServerPort;
            // The OS shortcut is the source of truth: a user may have toggled
            // it via Task Manager > Startup outside this app, so read the real
            // state rather than the persisted preference.
            LaunchAtStartupBox.IsChecked = StartupHelper.IsRegistered();
            LoadInstallInfo();
        }

        /// <summary>
        /// Populates the Installation Folder card (Llama page). The path is
        /// informational, not a setting: for an external (PATH) installation
        /// we show its directory but disable Empty — external installs are not
        /// LlamaApp's to delete. For the app-managed install, Empty is offered
        /// whenever the folder exists.
        /// </summary>
        private void LoadInstallInfo()
        {
            var mgr = Llama.LlamaManager.Shared;
            string path;
            bool canEmpty;

            if (mgr.CurrentOrigin == Llama.LlamaManager.Origin.External && mgr.BinaryPath is not null)
            {
                path = Path.GetDirectoryName(mgr.BinaryPath)!;
                InstallDescriptionText.Text =
                    "Using an external llama installation found on PATH. It isn't managed by LlamaApp — emptying is only available for the app-managed install.";
                canEmpty = false;
            }
            else
            {
                path = Llama.LlamaManager.ManagedInstallDir;
                InstallDescriptionText.Text =
                    "Where LlamaApp installs the llama server binary. Emptying frees disk space — the binary is downloaded again on next launch.";
                canEmpty = true;
            }

            InstallPathBox.Text = path;
            var exists = Directory.Exists(path);
            OpenInstallFolderButton.IsEnabled = exists;
            EmptyInstallFolderButton.IsEnabled = canEmpty && exists;
        }

        private async void OpenInstallFolder_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(InstallPathBox.Text);
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "open install folder failed");
            }
        }

        private async void OpenLogFolder_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(Common.Log.LogDirectory);
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "open log folder failed");
            }
        }

        private async void EmptyInstallFolder_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var path = InstallPathBox.Text;

            // Destructive: confirm first, with Cancel as the default button so
            // Enter can't trigger it accidentally.
            var confirm = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Empty Llama folder?",
                Content = $"This stops the llama server (if running) and deletes everything in:\n\n{path}\n\nThe llama binary is downloaded again the next time LlamaApp needs it.",
                PrimaryButtonText = "Empty",
                CloseButtonText = "Cancel",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                return;

            try
            {
                // Stop first: a running server holds a lock on llama.exe.
                Llama.LlamaManager.Shared.StopServer();

                // Empty = delete the contents, keep the folder itself.
                foreach (var dir in Directory.EnumerateDirectories(path))
                    Directory.Delete(dir, recursive: true);
                foreach (var file in Directory.EnumerateFiles(path))
                    File.Delete(file);

                Llama.LlamaManager.Shared.NotifyManagedInstallRemoved();
                Common.Log.Info($"emptied llama install folder: {path}");
                await ShowMessageAsync("Folder emptied",
                    "The llama binary will be downloaded again the next time it's needed.");
            }
            catch (Exception ex)
            {
                // The raw exception is logged, not shown — a user-facing
                // dialog gets an actionable message, not ex.Message.
                Common.Log.Warn(ex, "empty install folder failed");
                await ShowMessageAsync("Couldn't empty the folder",
                    "Some files may still be in use. Close any program using the folder and try again.");
            }

            LoadInstallInfo();
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var d = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK",
            };
            await d.ShowAsync();
        }

        private async void Browse_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // FolderPicker requires an owner HWND in unpackaged WinUI 3 apps.
            var hwnd = WindowNative.GetWindowHandle(this);
            var picker = new Windows.Storage.Pickers.FolderPicker();
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            try
            {
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                    CacheBox.Text = folder.Path;
            }
            catch
            {
                // Picker failed (e.g. cancelled / unsupported state) — leave the
                // current text untouched.
            }
        }

        private void Save_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var s = Settings.Current;
            s.HuggingFaceToken = TokenBox.Password;
            s.CacheDirectory = CacheBox.Text.Trim();

            // The NumberBox clamps to 1–65535 while editing; an empty box
            // reads as NaN — fall back to the default port in that case. The
            // value is applied the next time the app starts (the manager
            // singleton's port is fixed at construction).
            s.ServerPort = double.IsNaN(PortBox.Value)
                ? Llama.LlamaManager.DefaultServerPort
                : (int)PortBox.Value;

            // Apply the startup preference to the OS (create/delete the .lnk)
            // and mirror it into settings.json as a hint for the checkbox on
            // next open (LoadCurrent still re-reads the real OS state).
            var wantStartup = LaunchAtStartupBox.IsChecked == true;
            s.LaunchAtStartup = wantStartup;
            try
            {
                if (wantStartup) StartupHelper.Register();
                else StartupHelper.Unregister();
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "startup shortcut update failed");
            }

            s.Save();
            Close();
        }

        private void Cancel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Switches the visible settings page from the selected nav item's
        /// Tag. Null-guarded: the initial IsSelected in XAML can fire this
        /// during InitializeComponent, before the page fields are connected.
        /// </summary>
        private void NavView_SelectionChanged(
            Microsoft.UI.Xaml.Controls.NavigationView sender,
            Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (GeneralPage is null || IdentityPage is null || LlamaPage is null) return;

            var tag = (args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string;
            GeneralPage.Visibility = tag == "general"
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            IdentityPage.Visibility = tag == "identity"
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            LlamaPage.Visibility = tag == "llama"
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        // ---- Win32: center on the cursor's monitor ----

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        [DllImport("shcore.dll", ExactSpelling = true)]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}