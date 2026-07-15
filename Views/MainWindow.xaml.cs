using System.Collections.ObjectModel;
using System.Drawing;
using System.Runtime.InteropServices;
using LlamaApp.Common;
using LlamaApp.HuggingFace;
using LlamaApp.Llama;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace LlamaApp.Views
{
    /// <summary>
    /// Main application shell, repurposed as a single-view system-tray flyout.
    /// The window is never shown as a normal top-level window: it is styled
    /// borderless with a Mica backdrop (native Windows 11 flyout look) and only
    /// ever appears anchored to the tray icon via <see cref="ShowAsFlyout"/>,
    /// auto-hiding when it loses activation. This mirrors the macOS menu-bar
    /// app on Windows while hosting the four-section models panel.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        // Flyout dimensions, in physical pixels. Sized for the single-column
        // model list + footer; content scrolls if sections overflow.
        private const int FlyoutWidth = 420;
        private const int FlyoutHeight = 560;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        // How long after a deactivation-driven hide a tray click is treated as a
        // continuation of the click that dismissed the flyout (so it doesn't
        // bounce straight back open) rather than a fresh "open" request.
        private const long DeactivateHideGracePeriodMs = 300;

        // Deactivations arriving within this window after a show are treated as
        // the OS reclaiming foreground (a background process's Activate() can be
        // denied foreground, so the previously-active window snatches focus back
        // immediately) and ignored — without this the reshow would hide itself
        // straight back, making it look like the flyout never reopens.
        private const long ShownDeactivationGraceMs = 250;

        private const int SW_SHOW = 5;
        private const int SW_HIDE = 0;

        private bool _configured;
        private bool _activated;
        private bool _allowHideOnDeactivate;
        private long _lastDeactivateHideMs;
        private long _lastShownMs;
        private IntPtr _hwnd;

        /// <summary>
        /// Set by the tray manager when the app is truly exiting so the
        /// <see cref="Closed"/> handler lets the window close instead of hiding.
        /// </summary>
        public bool AllowClose { get; set; }

        /// <summary>
        /// Raised when the user picks <c>Quit</c> in the footer. Wired by
        /// <c>App</c> to <see cref="TrayIconManager.RequestExit"/> so the window
        /// doesn't need a direct reference to the tray-icon owner.
        /// </summary>
        public event Action? ExitRequested;

        /// <summary>Locally installed models — shown with a run glyph.</summary>
        public ObservableCollection<ModelItem> LocalModels { get; } = new();

        /// <summary>Recommended Hub models — shown with a download glyph.</summary>
        public ObservableCollection<ModelItem> RecommendedModels { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            ConfigureAsFlyout();
            Closed += MainWindow_Closed;
            Activated += MainWindow_Activated;

            LoadModels();
            UpdateEmptyState();
        }

        // ---- Data ----

        /// <summary>
        /// Populates the model lists. Placeholder data for now — will be
        /// replaced by a scan of the local Hugging Face cache and a catalog
        /// fetch once those integrations land.
        /// </summary>
        private void LoadModels()
        {
            // Local (Available) and Recommended models are both fetched async.
            _ = LoadLocalModelsAsync();
            _ = LoadRecommendedModelsAsync();
        }

        /// <summary>
        /// Scans the local Hugging Face cache (per <see cref="Settings.CacheDirectory"/>)
        /// for downloaded GGUF models and populates <see cref="LocalModels"/>. Fire-
        /// and-forget from the constructor; updates the UI incrementally.
        /// </summary>
        private async Task LoadLocalModelsAsync()
        {
            try
            {
                var cacheDir = Settings.Current.CacheDirectory;
                var repos = await LlamaApp.HuggingFace.Catalog.FetchLocalAsync(cacheDir);

                foreach (var repo in repos)
                {
                    var label = !string.IsNullOrEmpty(repo.DisplayName)
                        ? !string.IsNullOrEmpty(repo.Quant)
                            ? $"{repo.DisplayName} ({repo.Quant})"
                            : repo.DisplayName
                        : repo.Name;

                    LocalModels.Add(new ModelItem
                    {
                        Name = label,
                        RepoName = repo.Name,
                        Parameters = repo.Parameters,
                        Size = repo.Size,
                        License = repo.License,
                        Vision = repo.Vision,
                        Quant = repo.Quant,
                        Downloadable = false,
                        Brand = repo.Brand,
                        Logo = ModelItem.ResolveLogo(repo.Brand),
                    });
                }

                UpdateEmptyState();
            }
            catch
            {
                // Cache scan failure — leave the list empty ("No model yet").
                UpdateEmptyState();
            }
        }

        /// <summary>
        /// Fetches the remote catalog and populates <see cref="RecommendedModels"/>.
        /// Fire-and-forget from the constructor; the ObservableCollection updates
        /// the UI incrementally as entries arrive.
        /// </summary>
        private async Task LoadRecommendedModelsAsync()
        {
            try
            {
                var catalog = new LlamaApp.HuggingFace.Catalog();
                var repos = await Catalog.FetchAsync();

                // Build a display name that disambiguates quants: "GPT-OSS 20B (mxfp4)".
                foreach (var repo in repos)
                {
                    var label = !string.IsNullOrEmpty(repo.DisplayName)
                        ? !string.IsNullOrEmpty(repo.Quant)
                            ? $"{repo.DisplayName} ({repo.Quant})"
                            : repo.DisplayName
                        : repo.Name;

                    RecommendedModels.Add(new ModelItem
                    {
                        Name = label,
                        RepoName = repo.Name,
                        Parameters = repo.Parameters,
                        Size = repo.Size,
                        License = repo.License,
                        Vision = repo.Vision,
                        Quant = repo.Quant,
                        Downloadable = true,
                        Brand = repo.Brand,
                        Logo = ModelItem.ResolveLogo(repo.Brand),
                    });
                }
            }
            catch
            {
                // Network failure or parse error — leave the list empty; the
                // section still renders with its header. Could show an error
                // state here later.
            }
        }

        /// <summary>
        /// Shows/hides the "No model yet." placeholder based on whether any
        /// local models are present.
        /// </summary>
        private void UpdateEmptyState()
        {
            var empty = LocalModels.Count == 0;
            NoLocalModelsText.Visibility = empty ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            LocalModelsList.Visibility = empty ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        }

        // ---- Model download + launch ----

        /// <summary>
        /// Fired when a row in the Recommended Models section is tapped. Moves
        /// the model to the Available section with a progress ring, kicks off
        /// <see cref="LlamaManager.DownloadModelAsync"/> via the running llama
        /// server, then calls <see cref="LlamaManager.LaunchModelAsync"/> when
        /// the download completes.
        /// </summary>
        private void RecommendedModel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.FrameworkElement fe)
                return;
            if (fe.DataContext is not ModelItem item)
                return;
            if (item.IsDownloading)
                return; // already in flight (double-tap guard)

            // Move the model from Recommended → Available (downloading).
            RecommendedModels.Remove(item);
            item.Downloadable = false;
            item.IsDownloading = true;
            LocalModels.Add(item);
            UpdateEmptyState();

            _ = DownloadAndLaunchAsync(item);
        }

        /// <summary>
        /// Drives a single model's download → launch lifecycle. Reports
        /// progress to the <see cref="ModelItem.DownloadFraction"/> property
        /// (bound to the progress ring), then calls
        /// <see cref="LlamaManager.LaunchModelAsync"/> on success.
        /// </summary>
        private async Task DownloadAndLaunchAsync(ModelItem item)
        {
            var mgr = LlamaManager.Shared;
            var queue = DispatcherQueue; // marshal progress back to the UI thread
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                void Apply()
                {
                    if (p.TotalBytes > 0)
                        item.DownloadFraction = p.Fraction;
                }
                if (queue is null || queue.HasThreadAccess)
                    Apply();
                else
                    queue.TryEnqueue(() => Apply());
            });

            try
            {
                var ok = await mgr.DownloadModelAsync(item, progress);
                void Complete()
                {
                    item.IsDownloading = false;
                    if (ok)
                        _ = mgr.LaunchModelAsync(item); // download done — load it
                    else
                        item.DownloadFailed = true;
                }
                if (queue is null || queue.HasThreadAccess)
                    Complete();
                else
                    queue.TryEnqueue(Complete);
            }
            catch (OperationCanceledException)
            {
                if (queue is null || queue.HasThreadAccess)
                    item.IsDownloading = false;
                else
                    queue.TryEnqueue(() => item.IsDownloading = false);
            }
            catch
            {
                void Fail()
                {
                    item.IsDownloading = false;
                    item.DownloadFailed = true;
                }
                if (queue is null || queue.HasThreadAccess)
                    Fail();
                else
                    queue.TryEnqueue(Fail);
            }
        }

        // ---- Footer actions ----

        private void Settings_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Hide the flyout first so the settings dialog isn't drawn behind it
            // (the flyout would otherwise immediately deactivate and hide on its
            // own, but doing it explicitly avoids a flash).
            HideFlyout();

            var w = new SettingsWindow();
            w.Activate();
        }

        private void Quit_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            ExitRequested?.Invoke();
        }

        // ---- Flyout behavior ----

        /// <summary>
        /// Configures the WinUI window as a borderless, non-resizable flyout with
        /// no taskbar/Alt-Tab entry. Realizing the HWND up front (via
        /// <see cref="WindowNative.GetWindowHandle"/>) lets us position it before
        /// the first activation, so it never flashes at a default location.
        /// </summary>
        private void ConfigureAsFlyout()
        {
            if (_configured) return;
            _configured = true;

            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            presenter.SetBorderAndTitleBar(false, false); // borderless, no title bar → rounded corners + shadow on Win11
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            AppWindow.IsShownInSwitchers = false;   // remove from Alt-Tab / taskbar switcher
            AppWindow.Resize(new SizeInt32(FlyoutWidth, FlyoutHeight));

            // WS_EX_TOOLWINDOW keeps the window out of the taskbar entirely.
            _hwnd = WindowNative.GetWindowHandle(this);
            var ex = GetWindowLongCompat(_hwnd, GWL_EXSTYLE);
            SetWindowLongCompat(_hwnd, GWL_EXSTYLE, (IntPtr)(ex.ToInt32() | WS_EX_TOOLWINDOW));

            // WinUI 3 windows are WS_OVERLAPPEDWINDOW by default, and that style
            // keeps a thin frame (the white 1px edge around the Mica surface)
            // even when HasBorder is false — SetBorderAndTitleBar(false,false)
            // only hides the title bar / resize border, not this frame. The fix
            // is to switch the window style to WS_POPUP (a frameless popup) and
            // re-apply it with SetWindowPos(SWP_FRAMECHANGED), the same approach
            // H.NotifyIcon's borderless tray flyout uses. The compositor still
            // draws the rounded corners + drop shadow on Win11.
            const int GWL_STYLE = -16;
            SetWindowLongCompat(_hwnd, GWL_STYLE, new IntPtr(0x80000000L));
            const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
                       SWP_NOZORDER = 0x0004, SWP_NOOWNERZORDER = 0x0200,
                       SWP_FRAMECHANGED = 0x0020;
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);

            // DWM non-client rendering off — belt-and-suspenders with the popup
            // style above so no DWM border is drawn either.
            var ncrp = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));
        }

        /// <summary>
        /// Shows the flyout anchored near <paramref name="anchor"/> (the tray-icon
        /// click point, in physical screen coordinates). Pinned to the bottom-right
        /// of the nearest monitor's work area — just above the taskbar, next to
        /// the tray, where Windows 11 system-tray flyouts appear.
        /// </summary>
        public void ShowAsFlyout(Point anchor)
        {
            PositionNear(anchor);
            _lastShownMs = Environment.TickCount64;
            _allowHideOnDeactivate = false; // suppress deactivations during the show sequence

            if (!_activated)
            {
                _activated = true;
                // first-time activation shows the window at its set position
            }
            else
            {
                // Reshow: AppWindow.Show() alone is unreliable for a window that
                // was hidden while the process was in the background — Windows
                // may deny it foreground, so the previously-active window
                // snatches focus back and our Deactivated handler hides it again.
                // Mirror H.NotifyIcon's WindowExtensions.Show: drive both the
                // WinAppSDK and Win32 show state, then force foreground + activate.
                AppWindow.Show();
                ShowWindow(_hwnd, SW_SHOW);
                SetForegroundWindow(_hwnd);
            }

            Activate(); // first-time activation shows the window at its set position
        }

        /// <summary>Hides the flyout without closing it.</summary>
        void HideFlyout()
        {
            AppWindow.Hide();
            ShowWindow(_hwnd, SW_HIDE);
        }

        /// <summary>Whether the flyout is currently visible on screen.</summary>
        public bool IsFlyoutVisible => AppWindow.IsVisible;

        /// <summary>
        /// True when the flyout was hidden by a deactivation (i.e. the user
        /// clicked outside it, or clicked the tray icon) within the last grace
        /// period. Lets the tray left-click handler distinguish a click that
        /// *caused* the dismiss (don't reopen) from a fresh click a moment later
        /// (do open) — without this, clicking the icon to close would bounce the
        /// panel straight back open.
        /// </summary>
        public bool WasJustHiddenByDeactivate =>
            _lastDeactivateHideMs != 0 &&
            Environment.TickCount64 - _lastDeactivateHideMs < DeactivateHideGracePeriodMs;

        private void PositionNear(Point anchor)
        {
            AppWindow.Resize(new SizeInt32(FlyoutWidth, FlyoutHeight));

            // Pin the flyout to the bottom-right of the work area of the monitor
            // nearest the click — i.e. just above the taskbar, next to the tray.
            var work = GetWorkArea(anchor);

            var x = work.Right - FlyoutWidth;
            var y = work.Bottom - FlyoutHeight;

            AppWindow.Move(new PointInt32(x, y));
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Clicking anywhere outside the flyout deactivates it — dismiss,
                // the same way Windows 11 system-tray flyouts behave. Two guards:
                //   • _allowHideOnDeactivate suppresses a spurious deactivate
                //     that can race ahead of the show sequence.
                //   • The post-show grace swallows the focus-reclaim deactivation
                //     that hits a reshow when foreground lock denies us foreground
                //     (see ShowAsFlyout) — without it the reshow hides itself and
                //     looks like it never reopened.
                if (!_allowHideOnDeactivate ||
                    Environment.TickCount64 - _lastShownMs <= ShownDeactivationGraceMs) return;
                _allowHideOnDeactivate = false;
                _lastDeactivateHideMs = Environment.TickCount64;
                HideFlyout();
            }
            else
            {
                _allowHideOnDeactivate = true;
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // The app lives in the tray: a "close" (e.g. Alt+F4) just hides the
            // flyout unless the tray manager is shutting us down (AllowClose).
            if (AllowClose) return;
            args.Handled = true;
            HideFlyout();
        }

        // ---- Win32 interop: work-area lookup + extended window style ----

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

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hwnd, int nIndex, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_NCRENDERING_POLICY = 2;
        private const int DWMNCRP_DISABLED = 1;

        private static IntPtr GetWindowLongCompat(IntPtr hwnd, int nIndex) =>
            IntPtr.Size == 4 ? (IntPtr)GetWindowLong32(hwnd, nIndex) : GetWindowLongPtr64(hwnd, nIndex);

        private static void SetWindowLongCompat(IntPtr hwnd, int nIndex, IntPtr value)
        {
            if (IntPtr.Size == 4) SetWindowLong32(hwnd, nIndex, value.ToInt32());
            else SetWindowLongPtr64(hwnd, nIndex, value);
        }

        /// <summary>
        /// Returns the work area (excluding the taskbar) of the monitor nearest
        /// <paramref name="anchor"/>, in physical screen coordinates.
        /// </summary>
        private static RECT GetWorkArea(Point anchor)
        {
            var hmon = MonitorFromPoint(new POINT { X = anchor.X, Y = anchor.Y }, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hmon, ref mi);
            return mi.rcWork;
        }
    }
}
