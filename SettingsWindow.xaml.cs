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
    /// (Hugging Face token) and Llama (server port, listen address, idle model
    /// unload, KV cache quantization, custom serve arguments). Saves
    /// to <see cref="Settings"/> on Save; discards on Cancel.
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

        // Re-entrancy guard for the runtime-update Check button — a double
        // click must not start two checks (the second could race the first
        // install).
        private bool _runtimeCheckInFlight;

        public SettingsWindow()
        {
            InitializeComponent();
            Title = "Settings";
            Configure();
            SizeAndCenterOnScreen();
            LoadCurrent();
            UpdateRuntimeUpdateCard();

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

            // Pin the corner radius to the standard 8px "round" style rather
            // than relying on the system default.
            WindowCorners.ApplyRound8(this);
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
            PopulateListenAddressBox();
            SelectComboBoxTag(ListenAddressBox, s.ListenAddress, Common.ListenAddresses.Localhost);
            UpdateApiKeyPanel();
            // Select the idle-unload choice matching the saved seconds; an
            // unrecognized value (hand-edited settings.json) falls back to
            // Never, the safe default.
            var idleTag = s.IdleUnloadSeconds.ToString();
            foreach (var item in IdleUnloadBox.Items.OfType<Microsoft.UI.Xaml.Controls.ComboBoxItem>())
            {
                if (item.Tag as string == idleTag)
                {
                    IdleUnloadBox.SelectedItem = item;
                    break;
                }
            }
            if (IdleUnloadBox.SelectedItem is null)
                IdleUnloadBox.SelectedIndex = IdleUnloadBox.Items.Count - 1; // Never
            ModelsMaxBox.Value = s.MaxLoadedModels;
            SelectComboBoxTag(KvCacheKBox, s.CacheTypeK, "f16");
            SelectComboBoxTag(KvCacheVBox, s.CacheTypeV, "f16");
            CustomArgsBox.Text = s.CustomServeArguments ?? "";
            CustomArgsErrorText.Text = "";
            CustomArgsErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            // The OS shortcut is the source of truth: a user may have toggled
            // it via Task Manager > Startup outside this app, so read the real
            // state rather than the persisted preference.
            LaunchAtStartupBox.IsChecked = StartupHelper.IsRegistered();
            LoadInstallInfo();
        }

        /// <summary>
        /// Fills the Listen On ComboBox from the machine's network interfaces:
        /// the two pseudo-addresses first (all interfaces, localhost), then
        /// every up, non-loopback IPv4 interface. Each item carries the address
        /// in its Tag so Save_Click can persist exactly what the server will
        /// bind to.
        /// </summary>
        private void PopulateListenAddressBox()
        {
            ListenAddressBox.Items.Clear();
            foreach (var entry in Common.ListenAddresses.List())
            {
                ListenAddressBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{entry.Name} ({entry.Address})",
                    Tag = entry.Address,
                });
            }
            // Idempotent subscription — LoadCurrent may run more than once
            // over the window's lifetime.
            ListenAddressBox.SelectionChanged -= ListenAddressBox_SelectionChanged;
            ListenAddressBox.SelectionChanged += ListenAddressBox_SelectionChanged;
        }

        /// <summary>
        /// Shows the app-generated server API key when the selected (or saved)
        /// listen address exposes the server beyond this machine. llama.cpp's
        /// WebUI reads its key from a typed dialog — there is no URL form — so
        /// the user must be able to see and copy it; Llama's own client and the
        /// chat overlay connect with it automatically.
        /// </summary>
        private void UpdateApiKeyPanel()
        {
            var selected = (ListenAddressBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var needsKey = Common.ServerAuth.RequiresApiKey(selected);

            ApiKeyPanel.Visibility = needsKey
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
            if (!needsKey) return;

            // The key is generated at save/startup; before that the placeholder
            // says so.
            var key = Settings.Current.ServerApiKey;
            ApiKeyBox.Text = string.IsNullOrWhiteSpace(key) ? "" : key;
            CopyApiKeyButton.IsEnabled = !string.IsNullOrWhiteSpace(key);
        }

        private void ListenAddressBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateApiKeyPanel();

        /// <summary>Copies the server API key to the clipboard.</summary>
        private void CopyApiKey_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var key = ApiKeyBox.Text;
            if (string.IsNullOrWhiteSpace(key)) return;

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(key);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }

        /// <summary>
        /// Selects the ComboBox item whose <c>Tag</c> matches
        /// <paramref name="tag"/>; falls back to <paramref name="fallbackTag"/>
        /// (then to the first item) when the saved value isn't in the list —
        /// e.g. after a hand-edited settings.json or a version that lacked the
        /// value.
        /// </summary>
        private static void SelectComboBoxTag(ComboBox box, string? tag, string fallbackTag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag as string == tag)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag as string == fallbackTag)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0)
                box.SelectedIndex = 0;
        }

        /// <summary>
        /// Populates the Installation Folder card (Llama page). The path is
        /// informational, not a setting: for an external (PATH) installation
        /// we show its directory but disable Empty — external installs are not
        /// Llama's to delete. For the app-managed install, Empty is offered
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
                    "Using an external llama installation found on PATH. It isn't managed by Llama — emptying is only available for the app-managed install.";
                canEmpty = false;
            }
            else
            {
                path = Llama.LlamaManager.ManagedInstallDir;
                InstallDescriptionText.Text =
                    "Where Llama installs the llama server binary. Emptying frees disk space — the binary is downloaded again on next launch.";
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

        // ---- llama.cpp runtime update card ----

        /// <summary>
        /// Refreshes the runtime card's description line: the installed
        /// version (from the resolved binary) and when the weekly checker last
        /// ran. Harmless with no binary yet — the card says "unknown".
        /// </summary>
        private void UpdateRuntimeUpdateCard()
        {
            var version = Llama.LlamaManager.Shared.Version;
            var scheduler = (App.Current as App)?.RuntimeUpdates;
            var lastCheck = scheduler?.LastCheckUtc;
            RuntimeUpdateDescriptionText.Text =
                $"Llama keeps the llama.cpp runtime up to date with a weekly check. " +
                $"Installed: {(version is null ? "unknown" : version)}. " +
                $"Last checked: {(lastCheck is { } at ? at.LocalDateTime.ToString("g") : "never")}.";
        }

        /// <summary>
        /// The card's Check-for-updates button: runs the scheduler's manual
        /// check (same safety gates as the weekly one) and shows what came of
        /// it. The button + ring make the wait visible; an install can take a
        /// while on slow links. Re-entrancy guarded — a double click must not
        /// start two checks.
        /// </summary>
        private async void CheckRuntimeUpdate_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_runtimeCheckInFlight) return;
            var scheduler = (App.Current as App)?.RuntimeUpdates;
            if (scheduler is null) return;

            _runtimeCheckInFlight = true;
            CheckRuntimeUpdateButton.IsEnabled = false;
            CheckRuntimeUpdateRing.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CheckRuntimeUpdateRing.IsActive = true;
            CheckRuntimeUpdateResultText.Text = "Checking GitHub for a newer llama.cpp release…";
            try
            {
                var outcome = await scheduler.CheckNowAsync();
                CheckRuntimeUpdateResultText.Text = RuntimeUpdateMessages.Describe(outcome);
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "runtime update check threw");
                CheckRuntimeUpdateResultText.Text = "The check failed unexpectedly — try again later.";
            }
            finally
            {
                _runtimeCheckInFlight = false;
                CheckRuntimeUpdateButton.IsEnabled = true;
                CheckRuntimeUpdateRing.IsActive = false;
                CheckRuntimeUpdateRing.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                UpdateRuntimeUpdateCard();
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
                Content = $"This stops the llama server (if running) and deletes everything in:\n\n{path}\n\nThe llama binary is downloaded again the next time Llama needs it.",
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

        /// <summary>
        /// A yes/no confirmation. True only when the primary button is picked;
        /// closing the dialog (Esc / light-dismiss) is a cancel.
        /// </summary>
        private async Task<bool> ConfirmAsync(string title, string message)
        {
            var d = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = message,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            };
            var result = await d.ShowAsync();
            return result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
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

        private async void Save_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

            // The ComboBox items carry the IPv4 address in their Tag. Applied
            // the next time the app starts (the manager's bind address and REST
            // client are fixed at construction).
            var selectedListen = (ListenAddressBox.SelectedItem as ComboBoxItem)?.Tag as string
                ?? Common.ListenAddresses.Localhost;

            // Anything beyond loopback publishes the server to the network.
            // Make that an explicit, informed choice: confirm when the
            // selection moves off localhost (the app then protects the server
            // with an API key — see below).
            if (Common.ServerAuth.RequiresApiKey(selectedListen) &&
                !string.Equals(selectedListen, s.ListenAddress, StringComparison.OrdinalIgnoreCase))
            {
                bool confirmed;
                try
                {
                    confirmed = await ConfirmAsync(
                        "Expose the llama server?",
                        "Devices on your network will be able to reach the llama server. " +
                        "Llama protects it with an API key — shown in Settings so you can " +
                        "copy it if the WebUI asks — but anyone who learns the key " +
                        "can load, run and delete your models. Continue?");
                }
                catch (Exception ex)
                {
                    // A dialog can throw (e.g. another ContentDialog is open) —
                    // treat that as a cancel rather than faulting the save.
                    Common.Log.Warn(ex, "listen-address confirmation dialog failed");
                    return;
                }
                if (!confirmed) return;
            }

            s.ListenAddress = selectedListen;
            // Ensure an app-generated key exists for a non-loopback bind; it is
            // handed to the server via --api-key-file at the next launch and
            // surfaced in the Listen On card for copy.
            if (Common.ServerAuth.RequiresApiKey(selectedListen) && string.IsNullOrWhiteSpace(s.ServerApiKey))
            {
                s.ServerApiKey = Common.ServerAuth.GenerateApiKey();
                Common.Log.Info("generated an API key for the non-loopback listen address");
            }

            // The ComboBox items carry the seconds in their Tag. Applied the
            // next time the server starts (it's a launch argument), so no
            // live server restart here.
            if ((IdleUnloadBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag is string idleTag
                && int.TryParse(idleTag, out var idleSeconds))
                s.IdleUnloadSeconds = idleSeconds;

            // 0 = unlimited; the NumberBox enforces non-negative values, and an
            // empty box reads as NaN — fall back to the default (unlimited).
            s.MaxLoadedModels = double.IsNaN(ModelsMaxBox.Value)
                ? 0
                : (int)Math.Clamp(ModelsMaxBox.Value, 0, int.MaxValue);

            // The K/V ComboBox items carry the cache type in their Tag.
            // Applied the next time the server starts (launch arguments), so
            // no live server restart here.
            s.CacheTypeK = (KvCacheKBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "f16";
            s.CacheTypeV = (KvCacheVBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "f16";

            // Validate the free-form serve arguments before saving: an open
            // quote or a reserved flag (managed by the app — e.g. --host,
            // --port, --api-key) would otherwise only surface as a launch
            // failure after the user has already closed the window.
            try
            {
                var tokens = Common.ArgumentTokenizer.Tokenize(CustomArgsBox.Text);
                if (Common.ServeArgumentPolicy.Validate(tokens) is { } reservedError)
                    throw new FormatException(reservedError);
                CustomArgsErrorText.Text = "";
                CustomArgsErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            catch (FormatException ex)
            {
                CustomArgsErrorText.Text = ex.Message;
                CustomArgsErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }
            s.CustomServeArguments = CustomArgsBox.Text.Trim();

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