using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace LlamaApp
{
    /// <summary>
    /// A small centered settings window (separate from the tray flyout) for
    /// editing the Hugging Face token (password-masked) and the local models
    /// cache directory (with a folder picker). Saves to
    /// <see cref="Settings"/> on Save; discards on Cancel.
    /// </summary>
    public sealed partial class SettingsWindow : Window
    {
        private const int WindowWidth = 480;
        private const int WindowHeight = 380;

        public SettingsWindow()
        {
            InitializeComponent();
            Title = "LlamaApp Settings";
            Configure();
            CenterOnScreen();
            LoadCurrent();
        }

        private void Configure()
        {
            AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            // Don't appear in Alt-Tab / taskbar switcher — it's a child dialog
            // of the tray app, not a standalone top-level window.
            AppWindow.IsShownInSwitchers = false;
        }

        /// <summary>
        /// Centers the window on the monitor the cursor is on — the natural
        /// spot for a dialog spawned from a tray-only app (no owning window to
        /// center on).
        /// </summary>
        private void CenterOnScreen()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var cursor = GetCursorPos(out var pt) ? pt : new POINT();
            var hmon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hmon, ref mi);

            int x = mi.rcWork.Left + ((mi.rcWork.Right - mi.rcWork.Left) - WindowWidth) / 2;
            int y = mi.rcWork.Top + ((mi.rcWork.Bottom - mi.rcWork.Top) - WindowHeight) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }

        private void LoadCurrent()
        {
            var s = Settings.Current;
            TokenBox.Password = s.HuggingFaceToken ?? "";
            CacheBox.Text = s.CacheDirectory ?? "";
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