using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace LlamaApp
{
    /// <summary>
    /// A small centered settings window (separate from the tray flyout) for
    /// editing the Hugging Face token (password-masked), the local models
    /// cache directory (with a folder picker) and the llama server port.
    /// Saves to <see cref="Settings"/> on Save; discards on Cancel.
    /// </summary>
    public sealed partial class SettingsWindow : Window
    {
        // Settings window occupies 60% of the work-area width and 40% of its
        // height on whichever monitor the cursor is on (sized/centered on
        // construction in SizeAndCenterOnScreen).
        private const double WidthFraction = 0.50;
        private const double HeightFraction = 0.50;

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
        /// Sizes the window to WidthFraction × HeightFraction of the work area
        /// of the monitor the cursor is on, then centers it there — the
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
            int width = (int)(workWidth * WidthFraction);
            int height = (int)(workHeight * HeightFraction);

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

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}