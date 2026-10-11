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
            RuntimeBackendBox.SelectionChanged += (_, _) => UpdateGpuControls();
            GpuLayersBox.SelectionChanged += (_, _) => UpdateGpuControls();
            UpdateGpuControls();
            _ = PopulateGpuDevicesAsync();
            _ = PopulateInferenceCapabilitiesAsync();
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
            SelectComboBoxTag(ExperienceModeBox, SettingsModes.Normalize(s.ExperienceMode), SettingsModes.Simple);
            UpdateSettingsMode();
            TokenBox.Password = s.HuggingFaceToken ?? "";
            CacheBox.Text = s.CacheDirectory ?? "";
            AdditionalModelsFoldersPanel.Children.Clear();
            foreach (var directory in s.AdditionalModelDirectories)
                AddModelsFolderRow(directory);
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
            SelectComboBoxTag(RuntimeBackendBox, s.RuntimeBackend, Llama.InferenceRuntime.Automatic);
            var layerChoice = s.GpuLayers == "all" ? "all"
                : int.TryParse(s.GpuLayers, out _) ? "custom" : "auto";
            SelectComboBoxTag(GpuLayersBox, layerChoice, "auto");
            GpuLayersCountBox.Value = int.TryParse(s.GpuLayers, out var savedLayers) ? savedLayers : 32;
            BatchSizeBox.Value = s.BatchSize;
            MicroBatchSizeBox.Value = s.MicroBatchSize;
            SelectComboBoxTag(FlashAttentionBox, s.FlashAttention, "auto");
            var advanced = s.AdvancedInferenceProfile ?? new();
            ThreadsBox.Value = advanced.Threads;
            ThreadsBatchBox.Value = advanced.ThreadsBatch;
            ParallelBox.Value = advanced.Parallel;
            TemperatureBox.Value = advanced.Temperature ?? double.NaN;
            TopKBox.Value = advanced.TopK ?? double.NaN;
            TopPBox.Value = advanced.TopP ?? double.NaN;
            RepeatPenaltyBox.Value = advanced.RepeatPenalty ?? double.NaN;
            SelectComboBoxTag(SplitModeBox, advanced.SplitMode, "");
            TensorSplitBox.Text = advanced.TensorSplit ?? "";
            GpuDeviceBox.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "" });
            if (!string.IsNullOrWhiteSpace(s.GpuDeviceName))
                GpuDeviceBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{s.GpuDeviceName} (detecting…)",
                    Tag = s.GpuDeviceName,
                });
            SelectComboBoxTag(GpuDeviceBox, s.GpuDeviceName, "");
            GpuMemoryStatusText.Text = "Checking GPU memory…";
            CustomArgsBox.Text = s.CustomServeArguments ?? "";
            CustomArgsErrorText.Text = "";
            CustomArgsErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            EffectiveConfigurationBox.Text = Llama.LlamaManager.Shared.PreviewInferenceConfiguration();
            // The OS shortcut is the source of truth: a user may have toggled
            // it via Task Manager > Startup outside this app, so read the real
            // state rather than the persisted preference.
            LaunchAtStartupBox.IsChecked = StartupHelper.IsRegistered();
            LoadInstallInfo();
        }

        /// <summary>Shows each device reported by the installed llama binary.</summary>
        private async Task PopulateGpuDevicesAsync()
        {
            var savedName = Settings.Current.GpuDeviceName;
            try
            {
                var probe = await Llama.LlamaManager.Shared.ProbeDevicesAsync();
                var devices = probe.Devices.Where(device =>
                    device.Kind is not (Llama.DeviceKind.Cpu or Llama.DeviceKind.Unknown)).ToArray();
                GpuMemoryStatusText.Text = !probe.Succeeded
                    ? "Could not read GPU memory from the installed runtime."
                    : devices.Length == 0
                        ? "No GPUs were reported by the installed runtime."
                        : "Free memory is a snapshot from the installed runtime and can change. Restart after switching runtimes to refresh this list.";
                foreach (var device in devices)
                {
                    var key = Llama.GpuDeviceChoice.Key(device, devices);
                    var existing = GpuDeviceBox.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(item => item.Tag as string == key ||
                            item.Tag as string == savedName &&
                            Llama.GpuDeviceChoice.Resolve(savedName, devices) == device);
                    if (existing is not null)
                    {
                        existing.Content = Llama.GpuDeviceChoice.Label(device);
                        existing.Tag = key;
                    }
                    else GpuDeviceBox.Items.Add(new ComboBoxItem
                    {
                        Content = Llama.GpuDeviceChoice.Label(device),
                        Tag = key,
                    });
                }
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "GPU settings probe failed");
                GpuMemoryStatusText.Text = "Could not read GPU memory from the installed runtime.";
            }

            var savedItem = GpuDeviceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == savedName);
            if (savedItem is not null && (savedItem.Content as string)?.EndsWith("(detecting…)") == true)
                savedItem.Content = $"{savedName} (unavailable or ambiguous; choose a GPU again)";
        }

        private void UpdateGpuControls()
        {
            var cpu = (RuntimeBackendBox.SelectedItem as ComboBoxItem)?.Tag as string
                == Llama.InferenceRuntime.Cpu;
            GpuDeviceBox.IsEnabled = !cpu;
            GpuLayersBox.IsEnabled = !cpu;
            GpuLayersCountBox.IsEnabled = !cpu &&
                (GpuLayersBox.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        }

        private async Task PopulateInferenceCapabilitiesAsync()
        {
            if (Llama.LlamaManager.Shared.BinaryPath is not { } binary)
            {
                InferenceCapabilitiesText.Text = "Advanced options are checked when llama.cpp is installed.";
                return;
            }
            var caps = await Llama.ServeCapabilities.ProbeAsync(binary);
            if (!caps.Succeeded)
            {
                InferenceCapabilitiesText.Text = "Could not read the installed llama.cpp options.";
                return;
            }
            BatchSizeBox.IsEnabled = caps.Supports("--batch-size");
            MicroBatchSizeBox.IsEnabled = caps.Supports("--ubatch-size");
            FlashAttentionBox.IsEnabled = caps.Supports("--flash-attn");
            KvCacheKBox.IsEnabled = caps.Supports("--cache-type-k");
            KvCacheVBox.IsEnabled = caps.Supports("--cache-type-v");
            ThreadsBox.IsEnabled = caps.Supports("--threads");
            ThreadsBatchBox.IsEnabled = caps.Supports("--threads-batch");
            ParallelBox.IsEnabled = caps.Supports("--parallel");
            TemperatureBox.IsEnabled = caps.Supports("--temp");
            TopKBox.IsEnabled = caps.Supports("--top-k");
            TopPBox.IsEnabled = caps.Supports("--top-p");
            RepeatPenaltyBox.IsEnabled = caps.Supports("--repeat-penalty");
            SplitModeBox.IsEnabled = caps.Supports("--split-mode");
            TensorSplitBox.IsEnabled = caps.Supports("--tensor-split");
            InferenceCapabilitiesText.Text = "Unavailable controls are disabled for the installed llama.cpp build.";
        }

        private void ExperienceMode_SelectionChanged(object sender,
            Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e) => UpdateSettingsMode();

        private void RefreshConfiguration_Click(object sender,
            Microsoft.UI.Xaml.RoutedEventArgs e) =>
            EffectiveConfigurationBox.Text = Llama.LlamaManager.Shared.PreviewInferenceConfiguration();

        private void UpdateSettingsMode()
        {
            var mode = (ExperienceModeBox.SelectedItem as ComboBoxItem)?.Tag as string;
            AdvancedSettingsNotice.Visibility = !SettingsModes.ShowsAdvanced(mode) &&
                SettingsModes.HasSavedAdvancedSettings(Settings.Current)
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            var advanced = SettingsModes.ShowsAdvanced(mode)
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            InferenceHardwareCard.Visibility = advanced;
            ServerPortCard.Visibility = advanced;
            ListenAddressCard.Visibility = advanced;
            IdleUnloadCard.Visibility = advanced;
            MaxModelsCard.Visibility = advanced;
            KvCacheCard.Visibility = advanced;
            PromptProcessingCard.Visibility = advanced;
            CustomArgumentsCard.Visibility = SettingsModes.ShowsExpert(mode)
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            MultiGpuExpander.Visibility = SettingsModes.ShowsExpert(mode)
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
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

        private void AddModelsFolder_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => AddModelsFolderRow("");

        private void AddModelsFolderRow(string path)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star),
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });

            var box = new TextBox { Text = path, PlaceholderText = "Folder containing GGUF models" };
            Grid.SetColumn(box, 0);
            row.Children.Add(box);

            var browse = new Button { Content = "Browse…" };
            browse.Click += async (_, _) => await BrowseAdditionalModelsAsync(box);
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => AdditionalModelsFoldersPanel.Children.Remove(row);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            AdditionalModelsFoldersPanel.Children.Add(row);
        }

        private async Task BrowseAdditionalModelsAsync(TextBox target)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            try
            {
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null) target.Text = folder.Path;
            }
            catch (Exception ex) { Common.Log.Warn(ex, "additional models folder picker failed"); }
        }

        private async void Save_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var batchSize = double.IsNaN(BatchSizeBox.Value) ? 0
                : (int)Math.Clamp(BatchSizeBox.Value, 0, Llama.InferenceTuning.MaxBatchSize);
            var microBatchSize = double.IsNaN(MicroBatchSizeBox.Value) ? 0
                : (int)Math.Clamp(MicroBatchSizeBox.Value, 0, Llama.InferenceTuning.MaxBatchSize);
            var flashAttention = (FlashAttentionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            if (Llama.InferenceTuning.Validate(batchSize, microBatchSize, flashAttention) is { } tuningError)
            {
                InferenceTuningErrorText.Text = tuningError;
                InferenceTuningErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }
            var advancedProfile = new Llama.ModelPromptProcessingProfile
            {
                Threads = double.IsNaN(ThreadsBox.Value) ? 0 : (int)ThreadsBox.Value,
                ThreadsBatch = double.IsNaN(ThreadsBatchBox.Value) ? 0 : (int)ThreadsBatchBox.Value,
                Parallel = double.IsNaN(ParallelBox.Value) ? 0 : (int)ParallelBox.Value,
                Temperature = double.IsNaN(TemperatureBox.Value) ? null : TemperatureBox.Value,
                TopK = double.IsNaN(TopKBox.Value) ? null : (int)TopKBox.Value,
                TopP = double.IsNaN(TopPBox.Value) ? null : TopPBox.Value,
                RepeatPenalty = double.IsNaN(RepeatPenaltyBox.Value) ? null : RepeatPenaltyBox.Value,
                SplitMode = (SplitModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                TensorSplit = TensorSplitBox.Text.Trim(),
            };
            if (Llama.InferenceTuning.ValidateProfile(advancedProfile) is { } advancedError)
            {
                InferenceTuningErrorText.Text = advancedError;
                InferenceTuningErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }
            var proposedProfile = advancedProfile with
            {
                BatchSize = batchSize,
                MicroBatchSize = microBatchSize,
                FlashAttention = flashAttention,
                CacheTypeK = (KvCacheKBox.SelectedItem as ComboBoxItem)?.Tag as string is { } k && k != "f16" ? k : "",
                CacheTypeV = (KvCacheVBox.SelectedItem as ComboBoxItem)?.Tag as string is { } v && v != "f16" ? v : "",
            };
            if (!proposedProfile.IsAutomatic && Llama.LlamaManager.Shared.BinaryPath is { } binary)
            {
                var caps = await Llama.ServeCapabilities.ProbeAsync(binary);
                if (Llama.InferenceTuning.UnsupportedProfileOption(proposedProfile, caps) is { } unsupported)
                {
                    InferenceTuningErrorText.Text = unsupported.StartsWith("--", StringComparison.Ordinal)
                        ? $"The installed llama server does not support {unsupported}." : unsupported;
                    InferenceTuningErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    return;
                }
            }
            InferenceTuningErrorText.Text = "";
            InferenceTuningErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            // Finish validation before changing the in-memory settings singleton.
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

            var s = Settings.Current;
            s.ExperienceMode = SettingsModes.Normalize(
                (ExperienceModeBox.SelectedItem as ComboBoxItem)?.Tag as string);
            s.HuggingFaceToken = TokenBox.Password;
            s.CacheDirectory = CacheBox.Text.Trim();
            s.AdditionalModelDirectories = Settings.NormalizeAdditionalModelDirectories(
                AdditionalModelsFoldersPanel.Children.OfType<Grid>()
                    .Select(row => row.Children.OfType<TextBox>().First().Text));
            s.AdditionalModelsDirectory = null;

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

            s.CustomServeArguments = CustomArgsBox.Text.Trim();
            s.RuntimeBackend = (RuntimeBackendBox.SelectedItem as ComboBoxItem)?.Tag as string
                ?? Llama.InferenceRuntime.Automatic;
            s.GpuDeviceName = (GpuDeviceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            var layerChoice = (GpuLayersBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            s.GpuLayers = layerChoice == "custom"
                ? (double.IsNaN(GpuLayersCountBox.Value) ? "auto"
                    : ((int)Math.Clamp(GpuLayersCountBox.Value, 0, 1000)).ToString())
                : layerChoice;
            s.BatchSize = batchSize;
            s.MicroBatchSize = microBatchSize;
            s.FlashAttention = flashAttention;
            s.AdvancedInferenceProfile = advancedProfile;

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

            if (!s.Save())
            {
                SaveErrorText.Text = "Settings could not be saved. Check the app's data folder and try again.";
                SaveErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }
            var manager = Llama.LlamaManager.Shared;
            var foldersChanged = !(manager.AdditionalModelDirectories ?? [])
                .SequenceEqual(s.AdditionalModelDirectories, StringComparer.OrdinalIgnoreCase);
            manager.AdditionalModelDirectories = s.AdditionalModelDirectories.ToArray();
            if (foldersChanged)
                await manager.ReloadModelPresetsAsync();
            if (s.RuntimeBackend != manager.RuntimeBackend ||
                s.GpuDeviceName != manager.GpuDeviceName ||
                s.GpuLayers != manager.GpuLayers)
                Notifications.Show("Restart Llama to apply GPU settings",
                    "Exit Llama from the tray, then launch it again. The current model is still using the previous runtime and device.");
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
