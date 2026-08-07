using System.Collections.ObjectModel;
using System.Drawing;
using System.Reflection;
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
        // Flyout dimensions, in device-independent pixels (DIPs — the units XAML
        // layout uses). AppWindow sizes/positions are in PHYSICAL pixels, so these
        // are scaled by the target monitor's DPI before every Resize/Move — that
        // keeps the flyout the same logical size on every screen, no matter the
        // display's scaling (100% / 150% / 200% …). Sized for the single-column
        // model list + footer; content scrolls if sections overflow.
        private const int FlyoutWidthDips = 420;
        private const int FlyoutHeightDips = 560;

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
        public ObservableCollection<ModelItem> LocalModels { get; } = [];

        /// <summary>Recommended Hub models — shown with a download glyph.</summary>
        public ObservableCollection<ModelItem> RecommendedModels { get; } = [];

        /// <summary>
        /// The remote catalog, fetched once and shared by both sections: the
        /// Recommended section lists it directly, the Available section uses it
        /// to enrich server-reported models (display name, params, size, brand
        /// logo). Resolved before the per-section loaders run.
        /// </summary>
        private Task<List<Repository>> _catalogTask = null!;

        // <summary>
        // The catalog reshaped into a bare-repo-id → <see cref="Repository"/>
        // lookup, collapsing the per-quant duplicates. Built EXACTLY ONCE, as a
        // continuation over <see cref="_catalogTask"/> — the awaiters (initial
        // populate, the per-second poller reconcile, the download-row builders)
        // all share the same projected task, so the GroupBy/ToDictionary runs
        // once no matter how many callers race it or how often the 1s poller
        // fires. Previously <see cref="GetCatalogByRepoAsync"/> re-projected the
        // catalog on every call, allocating a fresh dictionary each second.
        // </summary>
        private Task<Dictionary<string, Repository>> _catalogByRepoTask = null!;

        // Re-entry guard for LoadLocalModelsAsync: 0 idle, 1 running.
        // StateChanged can re-trigger a load while an earlier invocation is
        // still waiting for the server, so we serialize population passes.
        private int _loadingLocalModels;

        // Index of Available rows by the server model id ("repo:quant"), so the
        // ModelsChanged poller can update each row's load state in place instead
        // of rebuilding the list every second (which would flicker and lose
        // click/loading state). Kept in sync wherever LocalModels is mutated.
        private readonly Dictionary<string, ModelItem> _localByServerId =
            new(StringComparer.OrdinalIgnoreCase);

        // Progress watches for downloads the app did not start itself (WebUI /
        // CLI), keyed by the row they feed. Such a row has no download driver
        // wiring byte progress, so without a watch it would sit on the
        // indeterminate ring for the whole download. The poller owns each
        // watch's lifetime: started when a row enters the downloading state,
        // canceled when it leaves it. All access happens on the UI thread.
        private readonly Dictionary<ModelItem, CancellationTokenSource> _externalDownloadWatches = new();

        // Last observed server state, for the once-per-transition crash toast
        // in LlamaManager_StateChanged (StateChanged fires for every manager
        // property change, not just status transitions).
        private LlamaManager.ServerState _lastServerStatus;

        // Delete-confirmation context: the trash button's attached Flyout opens
        // automatically on click; LocalModelDelete_Click captures the row's model
        // and the flyout here so the flyout's Delete button (which carries no
        // Tag of its own) can act on them and dismiss itself.
        private ModelItem? _pendingDelete;
        private Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? _deleteConfirmFlyout;

        // Hover fill for the model rows, resolved lazily from the theme resources.
        // Rows must keep a non-null Background at all times — a null Background
        // makes the Grid transparent to hit-testing, so PointerEntered would
        // never fire again after the first exit.
        private static Microsoft.UI.Xaml.Media.Brush? _rowHoverBrush;
        private static readonly Microsoft.UI.Xaml.Media.Brush RowRestBrush =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        public MainWindow()
        {
            InitializeComponent();
            ConfigureAsFlyout();

            // Brand logos: the bundled SVGs rasterize black (currentColor),
            // which vanishes on the dark theme's Mica — use the white ".light"
            // variants while the effective theme is dark, and re-resolve the
            // rows' logos live when the OS theme flips. Must run before
            // LoadModels() so the initial resolve picks the right variant.
            var root = (FrameworkElement)Content;
            ModelItem.UseLightLogos = root.ActualTheme == ElementTheme.Dark;
            root.ActualThemeChanged += (_, _) =>
            {
                ModelItem.UseLightLogos = root.ActualTheme == ElementTheme.Dark;
                ModelItem.ClearLogoCache();
                RefreshRowLogos();
            };

            Closed += MainWindow_Closed;
            Activated += MainWindow_Activated;

            LoadModels();
            LoadVersionInfo();
            UpdateServerStatusUI();
            UpdateEmptyState();
            _ = LoadAvatarAsync();

            // Refresh the footer's llama.cpp version as the binary is
            // detected/installed. LlamaManager.EnsureLlamaOrDownloadAsync runs
            // in parallel from App.OnLaunched; its StateChanged fires on the UI
            // thread, so we can touch the TextBlock directly.
            LlamaManager.Shared.StateChanged += LlamaManager_StateChanged;

            // The model-state poller (started by LlamaManager once the server is
            // Running) fires ModelsChanged roughly every 1s with a fresh /models
            // snapshot. We reconcile it into the Available rows in place —
            // flipping play -> indeterminate load ring -> OpenInNewWindow glyph
            // as the server reports each model's load state.
            LlamaManager.Shared.ModelsChanged += LlamaManager_ModelsChanged;
        }

        // ---- Data ----

        /// <summary>
        /// Populates the model lists. The Recommended section comes straight from
        /// the remote catalog; the Available section is fetched from the running
        /// llama server's <c>GET /models</c> once it's reachable. Both share a
        /// single catalog fetch (the Available rows are enriched from it).
        /// </summary>
        private void LoadModels()
        {
            StartCatalogFetch();
            _ = LoadRecommendedModelsAsync();
            _ = LoadLocalModelsAsync();
        }

        /// <summary>
        /// (Re)fetches the remote catalog and re-projects the repo-id lookup.
        /// Split out of <see cref="LoadModels"/> so the Recommended section's
        /// Retry button can refetch without touching the Available list.
        /// </summary>
        private void StartCatalogFetch()
        {
            _catalogTask = FetchCatalogAsync();
            // Project the fetched catalog into a repo-id lookup exactly once — a
            // continuation that runs when the fetch completes, shared by every
            // caller of GetCatalogByRepoAsync. ContinueWith(NotOnFaulted,
            // TaskScheduler.Default) so an (impossible — FetchCatalogAsync never
            // faults) fault still yields a usable empty dictionary rather than
            // a faulted task awaited by callers that don't expect a throw.
            _catalogByRepoTask = _catalogTask.ContinueWith(
                t => t.Result
                    .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Fetches the remote catalog once into <see cref="_catalogRepos"/> for
        /// both sections. Never throws — a network failure just yields an empty
        /// list (the Recommended section stays empty, Available rows aren't
        /// enriched).
        /// </summary>
        private async Task<List<Repository>> FetchCatalogAsync()
        {
            try { return (await Catalog.FetchAsync()).ToList(); }
            catch (Exception ex)
            {
                Log.Warn(ex, "catalog fetch failed; Recommended stays empty");
                return [];
            }
        }

        /// <summary>
        /// Lists the locally available (cached) models from the running llama
        /// server's <c>GET /models</c> endpoint — the authoritative source now
        /// that <see cref="LlamaManager"/> is a server client. Waits for the
        /// server to come up (<see cref="LlamaManager.EnsureLlamaOrDownloadAsync"/>
        /// runs in parallel from <c>App.OnLaunched</c>), then fetches. Each row is
        /// enriched with catalog metadata (display name, params, size, brand
        /// logo); the vision flag comes from the server's
        /// <c>architecture.input_modalities</c>.
        /// </summary>
        private async Task LoadLocalModelsAsync()
        {
            // Only one population pass at a time — LlamaManager_StateChanged can
            // re-trigger us while an earlier invocation is still waiting for the
            // server (or after a transient failure cleared up).
            if (Interlocked.CompareExchange(ref _loadingLocalModels, 1, 0) != 0) return;
            try
            {
                var mgr = LlamaManager.Shared;

                // Wait for the server to be reachable. A transient Failed here
                // isn't fatal: StateChanged re-triggers this once Running is
                // reached, so bail rather than block the full 5 minutes.
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                while (mgr.ServerStatus != LlamaManager.ServerState.Running)
                {
                    if (mgr.State == LlamaManager.InstallState.Failed ||
                        mgr.ServerStatus == LlamaManager.ServerState.Failed ||
                        DateTime.UtcNow >= deadline)
                    {
                        UpdateEmptyState();
                        return;
                    }
                    try { await Task.Delay(500); }
                    catch { return; }
                }

                // The router answers /health as soon as it binds, but /models
                // can come back empty for the first second or two while the HF
                // cache is scanned. Retry briefly so a startup race doesn't pin
                // the list to "No model yet" forever.
                IReadOnlyList<LlamaManager.ServerModel> serverModels = [];
                var modelDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < modelDeadline)
                {
                    try { serverModels = await mgr.GetModelsAsync(); }
                    catch (Exception ex) { Log.Debug("GetModels retry failed: " + ex.Message); serverModels = []; }
                    
                    if (serverModels.Count > 0) break;
                    try { await Task.Delay(500); }
                    catch { break; }
                }

                await PopulateLocalModelsAsync(serverModels);
                Log.Info("loaded " + serverModels.Count + " local model(s) from the server");
            }
            finally
            {
                Interlocked.Exchange(ref _loadingLocalModels, 0);
            }
        }

        /// <summary>
        /// Replaces <see cref="LocalModels"/> with one row per server-reported
        /// model, enriched with catalog metadata (display name, params, size,
        /// brand logo); the vision flag comes from the server's
        /// <c>architecture.input_modalities</c>. Idempotent — clears before
        /// adding so repeated calls (e.g., on StateChanged) don't accumulate
        /// duplicates. Runs on the UI thread (callers await on it).
        /// </summary>
        private async Task PopulateLocalModelsAsync(
            IReadOnlyList<LlamaManager.ServerModel> serverModels)
        {
            var byRepo = await GetCatalogByRepoAsync();

            LocalModels.Clear();
            _localByServerId.Clear();
            foreach (var sm in serverModels)
            {
                var item = BuildLocalItem(sm, byRepo);
                _localByServerId[sm.Id] = item;
                LocalModels.Add(item);
            }

            UpdateEmptyState();
        }

        /// <summary>
        /// Returns the cached catalog reshaped into a bare-repo-id →
        /// <see cref="Repository"/> lookup, collapsing the per-quant duplicates
        /// (a repo can appear several times in the flattened catalog). The
        /// projection is materialized once by a continuation over
        /// <see cref="_catalogTask"/> in <see cref="LoadModels"/>; callers just
        /// await the shared <see cref="_catalogByRepoTask"/> so a high-frequency
        /// caller (the 1s <c>/models</c> poller via <see cref="ReconcileAsync"/>)
        /// doesn’t re-GroupBy the catalog on every tick.
        /// </summary>
        private Task<Dictionary<string, Repository>> GetCatalogByRepoAsync() => _catalogByRepoTask;

        /// <summary>
        /// Builds an enriched <see cref="ModelItem"/> for a server-reported
        /// model (display name, params, size, brand/logo from the catalog; vision
        /// and load state from the server snapshot). Seeds <see cref="ModelItem.IsLoaded"/>
        /// so an already-loaded model lands straight on the OpenInNewWindow glyph.
        /// </summary>
        private static ModelItem BuildLocalItem(
            LlamaManager.ServerModel sm, Dictionary<string, Repository> byRepo)
        {
            var (repo, quant) = SplitServerId(sm.Id);
            byRepo.TryGetValue(repo, out var matched);
            return new ModelItem
            {
                Name = DeriveDisplayName(repo, byRepo),
                RepoName = repo,
                Quant = quant,
                Description = matched?.Description ?? "",
                Parameters = matched?.Parameters ?? "",
                Size = matched?.Size ?? "",
                License = matched?.License ?? "",
                Vision = sm.SupportsImage, // authoritative — from the server
                Downloadable = false,
                Brand = matched?.Brand,
                Logo = ModelItem.ResolveLogo(matched?.Brand),
                IsLoaded = sm.IsLoaded,
                IsDownloading = sm.IsDownloading,
            };
        }

        /// <summary>
        /// Splits a server model id (<c>repo</c> or <c>repo:quant</c>) into the
        /// bare HF repo id and the quant label (empty when absent).
        /// </summary>
        private static (string repo, string quant) SplitServerId(string id)
        {
            var idx = id.IndexOf(':');
            return idx < 0 ? (id, "") : (id[..idx], id[(idx + 1)..]);
        }

        /// <summary>
        /// Builds a display name for a server-reported model: the catalog's
        /// <c>DisplayName</c> when known, else the last path segment of the
        /// repo id. The quant is deliberately not shown — it's an
        /// implementation detail of the server model id, not friendly row text.
        /// </summary>
        private static string DeriveDisplayName(
            string repo, Dictionary<string, Repository> byRepo)
        {
            byRepo.TryGetValue(repo, out var matched);
            return !string.IsNullOrEmpty(matched?.DisplayName)
                ? matched.DisplayName
                : repo.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? repo;
        }

        /// <summary>
        /// Fetches the remote catalog and populates <see cref="RecommendedModels"/>.
        /// Shares the single catalog fetch with the Available section.
        /// </summary>
        private async Task LoadRecommendedModelsAsync()
        {
            List<Repository> repos;
            try { repos = await _catalogTask; }
            catch (Exception ex) { Log.Warn(ex, "recommended models load failed"); repos = []; }

            // An empty catalog after the fetch means it couldn't be loaded
            // (network/parse failure) — say so and offer a retry instead of
            // leaving the section silently blank.
            if (repos.Count == 0)
            {
                RecommendedScroll.Visibility = Visibility.Collapsed;
                RecommendedStatusPanel.Visibility = Visibility.Visible;
                RecommendedStatusText.Text = "Couldn't load the model catalog. Check your connection and try again.";
                RetryCatalogButton.Visibility = Visibility.Visible;
                return;
            }

            RecommendedModels.Clear(); // idempotent — safe on catalog retry

            // One row per repo: the flattened catalog lists a repo once per
            // build (quant), which would show as identical duplicate rows now
            // that the quant isn't displayed. Take the first build in catalog
            // order — its Quant still drives ServerModelId for the download,
            // it just isn't shown. Featured families sort first (GroupBy
            // preserves the order of first appearance).
            foreach (var repo in RecommendedOrdering.OrderForDisplay(repos)
                         .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                var label = !string.IsNullOrEmpty(repo.DisplayName)
                    ? repo.DisplayName
                    : repo.Name;

                RecommendedModels.Add(new ModelItem
                {
                    Name = label,
                    RepoName = repo.Name,
                    Description = repo.Description,
                    Parameters = repo.Parameters,
                    Size = repo.Size,
                    SizeBytes = repo.SizeBytes,
                    License = repo.License,
                    Vision = repo.Vision,
                    Quant = repo.Quant,
                    Downloadable = true,
                    Brand = repo.Brand,
                    Logo = ModelItem.ResolveLogo(repo.Brand),
                });
            }

            RecommendedStatusPanel.Visibility = Visibility.Collapsed;
            RetryCatalogButton.Visibility = Visibility.Collapsed;
            RecommendedScroll.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Retry button shown when the catalog fetch fails: refetches the catalog
        /// and repopulates the Recommended section. The Available list is left
        /// alone — it comes from the running server, not the catalog.
        /// </summary>
        private void RetryCatalog_Click(object sender, RoutedEventArgs e)
        {
            RecommendedStatusText.Text = "Loading models…";
            RetryCatalogButton.Visibility = Visibility.Collapsed;
            StartCatalogFetch();
            _ = LoadRecommendedModelsAsync();
        }

        /// <summary>
        /// Shows/hides the "No model yet." placeholder based on whether any
        /// local models are present.
        /// </summary>
        private void UpdateEmptyState()
        {
            var empty = LocalModels.Count == 0;
            NoLocalModelsText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LocalModelsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (empty)
            {
                // Honest per-state text (mapping rules live in
                // EmptyStatePresentation so they stay unit-testable): while the
                // server is still coming up the list may fill shortly — say so;
                // once it's running, point at the Recommended section; on a
                // crash / failed install, say that instead of claiming the
                // server is still starting forever.
                NoLocalModelsText.Text = EmptyStatePresentation.Describe(
                    LlamaManager.Shared.ServerStatus, LlamaManager.Shared.State);
            }
        }

        // ---- Model download + launch ----

        /// <summary>
        /// Fired when a row in the Recommended Models section is invoked —
        /// clicked, or Enter/Space on the focused row (the row root is a
        /// chromeless Button, so keyboard and screen-reader invokes land here
        /// too). Moves the model to the Available section with a progress ring,
        /// kicks off <see cref="LlamaManager.DownloadModelAsync"/> via the
        /// running llama server, then loads it (see
        /// <see cref="LoadAndWatchAsync"/>) when the download completes — the
        /// row transitions download ring -> load ring -> OpenInNewWindow glyph.
        /// </summary>
        private void RecommendedModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe)
                return;
            
            // x:Bind doesn't set DataContext inside a DataTemplate (compiled
            // bindings bypass the property), so read the row's model from its
            // Tag (bound via Tag="{x:Bind}") and fall back to a visual-tree
            // walk — same approach as the Available-row play/open buttons.
            if (ResolveRowItem(fe) is not { } item)
                return;
            
            if (item.IsDownloading)
                return; // already in flight (double-tap guard)

            // Disk-space preflight: a one-tap row starts a multi-GB download,
            // so block it up front when the cache drive can't hold the model
            // (an unknown size or a failed probe never blocks).
            if (item.SizeBytes > 0 &&
                !Common.DiskSpace.HasEnoughSpace(
                    Settings.Current.CacheDirectory, item.SizeBytes, out var freeBytes))
            {
                Log.Info($"download blocked: {((IModel)item).ServerModelId} needs " +
                    $"{item.SizeBytes} bytes, only {freeBytes} free");
                ShowInsufficientSpaceFlyout(fe, item, freeBytes);
                return;
            }

            // Move the model from Recommended → Available (downloading).
            RecommendedModels.Remove(item);
            item.Downloadable = false;
            item.IsDownloading = true;
            _localByServerId[((IModel)item).ServerModelId] = item;
            LocalModels.Add(item);
            UpdateEmptyState();

            _ = DownloadAndLaunchAsync(item);
        }

        /// <summary>
        /// Shows a light-dismiss flyout on the tapped Recommended row when the
        /// disk-space preflight blocks a download. A flyout (not a dialog) so
        /// the tray window's hide-on-deactivate can't strand a modal.
        /// </summary>
        private static void ShowInsufficientSpaceFlyout(FrameworkElement target, ModelItem item, long freeBytes)
        {
            // Free space is realistically GB-scale; drop to MB below that so a
            // nearly-full drive doesn't read "0 GB".
            var freeText = freeBytes >= 1_000_000_000
                ? $"{freeBytes / 1_000_000_000.0:0.#} GB"
                : $"{Math.Max(0, freeBytes) / 1_000_000.0:0} MB";

            var flyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 6,
                    MaxWidth = 260,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Not enough disk space",
                            FontSize = 13,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = $"{item.DisplayName} needs {item.Size}, but only " +
                                   $"{freeText} is free. Free up space, or change the " +
                                   "cache folder in Settings.",
                            FontSize = 12,
                            Opacity = 0.7,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            flyout.ShowAt(target);
        }

        /// <summary>
        /// Drives a single model's download → load lifecycle. Reports
        /// progress to the <see cref="ModelItem.DownloadFraction"/> property
        /// (bound to the download progress ring), then on success flips the row
        /// into the loading state, and asks the server to load it (see
        /// <see cref="LoadAndWatchAsync"/>).
        /// </summary>
        private async Task DownloadAndLaunchAsync(ModelItem item)
        {
            var mgr = LlamaManager.Shared;
            var queue = DispatcherQueue; // marshal progress back to the UI thread

            // Per-download cancellation: the row's cancel button cancels this
            // source. DownloadModelAsync closes the SSE stream and asks the
            // server to abort the download when the token fires.
            using var cts = new CancellationTokenSource();
            item.DownloadCancellation = cts;
            // Reset any stale detail from a previous (failed) attempt — the
            // subtitle shows the live detail line as soon as a size is known.
            item.DownloadedBytes = 0;
            item.DownloadTotalBytes = 0;
            item.DownloadBytesPerSecond = 0;

            // Throttle UI updates: the server streams an SSE progress event per
            // chunk (potentially hundreds/sec), and each one would otherwise
            // enqueue a UI-thread callback. The ring + percent caption only need
            // ~10 updates/sec. Terminal events (Done/Failed) always pass through
            // so the final state lands immediately.
            long lastProgressApplyMs = 0;
            long lastSampleBytes = 0, lastSampleMs = 0;
            double bytesPerSecond = 0;
            string? serverMessage = null;
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                var now = Environment.TickCount64;
                if (!p.Done && !p.Failed && now - lastProgressApplyMs < 100) return;
                lastProgressApplyMs = now;

                // The server's rejection detail (POST error body, stream
                // failure) — surfaced in the failure toast.
                if (p.Failed && !string.IsNullOrWhiteSpace(p.Message))
                    serverMessage = p.Message;

                // Speed estimate between applied samples (EMA-smoothed — the
                // per-chunk instantaneous rate jitters too much to show raw).
                if (p.DownloadedBytes > 0)
                {
                    if (lastSampleMs != 0 && p.DownloadedBytes > lastSampleBytes)
                    {
                        var instantaneous = (p.DownloadedBytes - lastSampleBytes)
                            * 1000.0 / Math.Max(1, now - lastSampleMs);
                        bytesPerSecond = DownloadProgressPresentation
                            .SmoothSpeed(bytesPerSecond, instantaneous);
                    }
                    lastSampleBytes = p.DownloadedBytes;
                    lastSampleMs = now;
                }

                void Apply()
                {
                    if (p.TotalBytes > 0)
                    {
                        item.DownloadFraction = p.Fraction;
                        item.DownloadedBytes = p.DownloadedBytes;
                        item.DownloadTotalBytes = p.TotalBytes;
                        item.DownloadBytesPerSecond = bytesPerSecond;
                    }
                }
                if (queue is null || queue.HasThreadAccess)
                    Apply();
                else
                    queue.TryEnqueue(Apply);
            });

            try
            {
                var ok = await mgr.DownloadModelAsync(item, progress, cts.Token);
                void Complete()
                {
                    item.IsDownloading = false;
                    if (ok)
                    {
                        // Download done — load it. The row now shows the
                        // load ring until the poller reports the model as
                        // loaded.
                        item.LoadFailed = false;
                        item.IsLoading = true;
                        _ = LoadAndWatchAsync(item);
                    }
                    else
                    {
                        item.DownloadFailed = true;
                        NotifyWhenHidden("Download failed",
                            DownloadFailureToastBody(item, serverMessage));
                    }
                }
                if (queue is null || queue.HasThreadAccess)
                    Complete();
                else
                    queue.TryEnqueue(Complete);
            }
            catch (OperationCanceledException)
            {
                // User canceled from the row's cancel button — the server has
                // already been asked to abort (see DownloadModelAsync). Return
                // the row to the play glyph; a partial download resumes on the
                // next attempt.
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
                    NotifyWhenHidden("Download failed",
                        DownloadFailureToastBody(item, serverMessage));
                }
                if (queue is null || queue.HasThreadAccess)
                    Fail();
                else
                    queue.TryEnqueue(Fail);
            }
            finally
            {
                // Clear before the `using` disposes so a late cancel click can
                // never touch a disposed source.
                item.DownloadCancellation = null;
            }
        }

        /// <summary>
        /// Builds the download-failure toast body, appending the server's
        /// rejection detail when one was reported (truncated so a JSON error
        /// body doesn't flood the toast).
        /// </summary>
        private static string DownloadFailureToastBody(ModelItem item, string? serverMessage)
        {
            var detail = "";
            if (!string.IsNullOrWhiteSpace(serverMessage))
            {
                var trimmed = serverMessage.Length > 140
                    ? serverMessage[..140] + "…"
                    : serverMessage;
                detail = $" Server said: {trimmed}.";
            }
            return $"{item.DisplayName} couldn't be downloaded.{detail} Click retry to try again.";
        }

        // ---- Model load → open ----

        /// <summary>
        /// Fired when the play glyph on an Available (local) row is tapped.
        /// Asks the running llama server to load the model and flips the row into
        /// the loading state (indeterminate ring) until the poller reports it as
        /// loaded. No-op if the row is already loading/loaded/downloading.
        /// </summary>
        private void LocalModelPlay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe)
            {
                Log.Warn("sender is not a FrameworkElement");
                return;
            }
            // x:Bind doesn't set DataContext on child elements inside a
            // DataTemplate (compiled bindings bypass the property), so read
            // the row's model from the bound Tag instead and fall back to a
            // visual-tree walk.
            if (ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem (Tag=" +
                    (fe.Tag?.GetType().FullName ?? "null") + ")");
                return;
            }
            if (item.IsLoading || item.IsLoaded || item.IsDownloading)
            {
                Log.Debug("ignored (isLoading=" + item.IsLoading +
                    " isLoaded=" + item.IsLoaded + " isDownloading=" + item.IsDownloading + ")");
                return;
            }

            Log.Info("play clicked: loading " + ((IModel)item).ServerModelId);
            item.LoadFailed = false;
            item.IsLoading = true;
            _ = LoadAndWatchAsync(item);
        }

        /// <summary>
        /// Fired when the retry glyph on a load-failed row is tapped: clears the
        /// failure state and re-attempts the load. (A load-failed row shows
        /// warning + retry instead of the play glyph so a rejected load — OOM,
        /// corrupt GGUF, server refusal — isn't silent.)
        /// </summary>
        private void LocalModelRetryLoad_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }
            if (item.IsLoading || item.IsLoaded || item.IsDownloading || !item.LoadFailed)
                return;

            Log.Info("retry clicked: re-loading " + ((IModel)item).ServerModelId);
            item.LoadFailed = false;
            item.IsLoading = true;
            _ = LoadAndWatchAsync(item);
        }

        /// <summary>
        /// Fired when the cancel glyph next to the download ring is tapped:
        /// cancels the in-flight download. The server is asked to abort too
        /// (see <see cref="LlamaManager.DownloadModelAsync"/>); the row returns
        /// to the play glyph and a partial download resumes on the next attempt.
        /// </summary>
        private void LocalModelCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }

            Log.Info("cancel clicked: cancelling download of " + ((IModel)item).ServerModelId);
            try { item.DownloadCancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* download finished between check and click */ }
        }

        /// <summary>
        /// Fired when the retry glyph on a failed-download row is tapped: clears
        /// the failure state and restarts the download → load lifecycle. (A
        /// failed row shows warning + retry instead of the play glyph — the
        /// model isn't fully cached, so loading it would just be rejected.)
        /// </summary>
        private void LocalModelRetryDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }
            if (item.IsDownloading || !item.DownloadFailed) return;

            Log.Info("retry clicked: re-downloading " + ((IModel)item).ServerModelId);
            item.DownloadFailed = false;
            item.IsDownloading = true;
            _ = DownloadAndLaunchAsync(item);
        }

        /// <summary>
        /// Fired when the trash glyph on an Available row is tapped. The button's
        /// attached confirmation Flyout opens automatically on the click; this
        /// just captures the row's model and the flyout so
        /// <see cref="LocalModelDeleteConfirm_Click"/> can act on them. Deleting
        /// means re-downloading GBs, so it never happens on a single misclick.
        /// </summary>
        private void LocalModelDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            _pendingDelete = ResolveRowItem(btn);
            // Must read Button.Flyout, not FlyoutBase.GetAttachedFlyout: the
            // flyout is set via the <Button.Flyout> property element, which is
            // Button's own property — GetAttachedFlyout reads the separate
            // FlyoutBase.AttachedFlyout attached property and returns null
            // here, which made the confirm handler's Hide() a silent no-op
            // (the flyout stayed open after clicking Delete).
            _deleteConfirmFlyout = btn.Flyout;
        }

        /// <summary>
        /// The Delete button inside the trash glyph's confirmation flyout:
        /// deletes the model from the running llama server's cache — sends
        /// <c>DELETE /models/{name}</c>; on success the row is removed from
        /// <see cref="LocalModels"/> immediately (the poller's next tick would
        /// drop it too, but removing now avoids a stale row lingering for up to
        /// one poll interval). No-op if the row is loaded or loading — a
        /// resident model must be unloaded first.
        /// </summary>
        private async void LocalModelDeleteConfirm_Click(object sender, RoutedEventArgs e)
        {
            _deleteConfirmFlyout?.Hide();
            if (_pendingDelete is not { } item) return;
            _pendingDelete = null;

            if (item.IsLoaded || item.IsLoading || item.IsDownloading)
            {
                Log.Debug("ignored delete (isLoading=" + item.IsLoading +
                    " isLoaded=" + item.IsLoaded + " isDownloading=" + item.IsDownloading + ")");
                return;
            }

            Log.Info("delete confirmed: removing " + ((IModel)item).ServerModelId);
            if (await LlamaManager.Shared.DeleteModelAsync(item))
            {
                _localByServerId.Remove(((IModel)item).ServerModelId);
                LocalModels.Remove(item);
                UpdateEmptyState();
            }
            else
            {
                Log.Warn("server rejected delete for " + ((IModel)item).ServerModelId);
            }
        }

        /// <summary>
        /// Opens the running llama server's WebUI in the system browser for the
        /// selected model — the action behind the OpenInNewWindow glyph on a
        /// loaded Available row. Passes <c>?model=&lt;ServerModelId&gt;</c> so the
        /// server loads the requested model automatically.
        /// </summary>
        private async void LocalModelOpen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }

            var serverModelId = ((IModel)item).ServerModelId;
            Log.Info("open clicked for " + serverModelId);
            
            var url = $"http://localhost:{LlamaManager.Shared.ServerPort}?model={Uri.EscapeDataString(serverModelId)}";
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }

        /// <summary>
        /// Unloads a loaded model from the running llama server — the action behind
        /// the power glyph next to the OpenInNewWindow glyph on a loaded Available
        /// row. Sends <c>POST /models/unload</c> and clears the row's loaded state
        /// once the server accepts the request; the poller will confirm the status
        /// change on its next tick.
        /// </summary>
        private async void LocalModelUnload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }
            if (!item.IsLoaded)
            {
                Log.Debug("ignored (not loaded)");
                return;
            }

            Log.Info("unload clicked: unloading " + ((IModel)item).ServerModelId);
            if (await LlamaManager.Shared.UnloadModelAsync(item))
            {
                item.IsLoaded = false;
                item.IsLoading = false;
            }
            else
            {
                Log.Warn("server rejected unload for " + ((IModel)item).ServerModelId);
            }
        }

        // ---- Toast notifications ----

        /// <summary>
        /// Shows a toast for a background event the user is likely waiting on,
        /// but only when the flyout is hidden — when they're watching the panel,
        /// the row state already tells the story and a toast would be noise.
        /// </summary>
        private void NotifyWhenHidden(string title, string body)
        {
            if (!IsFlyoutVisible)
                Notifications.Show(title, body);
        }

        // ---- Row hover feedback ----

        /// <summary>
        /// Re-resolves every row's brand logo after a theme change (the logo
        /// variant is theme-dependent and the cache has just been cleared).
        /// </summary>
        private void RefreshRowLogos()
        {
            foreach (var item in LocalModels.Concat(RecommendedModels))
                item.Logo = ModelItem.ResolveLogo(item.Brand);
        }

        /// <summary>
        /// Paints the hovered model row with the theme's subtle fill so rows read
        /// as interactive (the Recommended rows are fully tappable; the Available
        /// rows host small icon buttons). The brush is resolved once from the app
        /// resources, which consult the active theme dictionary.
        /// </summary>
        private void Row_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is not Grid row) return;
            _rowHoverBrush ??= (Microsoft.UI.Xaml.Media.Brush)Application.Current
                .Resources["SubtleFillColorSecondaryBrush"];
            row.Background = _rowHoverBrush;
        }

        /// <summary>
        /// Restores the row's resting background. Transparent, not null — a null
        /// Background makes the Grid invisible to hit-testing, so the next
        /// PointerEntered would never fire.
        /// </summary>
        private void Row_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid row) row.Background = RowRestBrush;
        }

        /// <summary>
        /// Resolves the <see cref="ModelItem"/> a click came from. <c>x:Bind</c>
        /// doesn't propagate <c>DataContext</c> to child elements inside a
        /// <c>DataTemplate</c> (compiled bindings bypass the property), so the
        /// row's model is bound to the element's <c>Tag</c> via <c>Tag="{x:Bind}"</c>;
        /// this reads it. Falls back to a visual-tree walk (the <c>ItemsRepeater</c>
        /// sets <c>DataContext</c> on the row's root element) so it also works for
        /// elements that didn't bind <c>Tag</c>.
        /// </summary>
        private static ModelItem? ResolveRowItem(Microsoft.UI.Xaml.FrameworkElement fe)
        {
            if (fe.Tag is ModelItem tagItem) return tagItem;
            for (var el = fe; el is not null; el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el)
                as Microsoft.UI.Xaml.FrameworkElement)
            {
                if (el.DataContext is ModelItem dcItem) return dcItem;
            }
            return null;
        }

        /// <summary>
        /// Sends a <c>POST /models/load</c> for <paramref name="item"/> and, on
        /// rejection, clears the optimistic <see cref="ModelItem.IsLoading"/> so
        /// the row falls back to the play glyph. While the load runs, the
        /// server's <c>status_change</c> SSE events drive the row's load ring
        /// via <see cref="ModelItem.LoadFraction"/>; the
        /// <see cref="LlamaManager.ModelsChanged"/> poller owns the final
        /// transition (setting <see cref="ModelItem.IsLoaded"/> and clearing
        /// <see cref="ModelItem.IsLoading"/> via <see cref="ReconcileAsync"/>).
        /// </summary>
        private async Task LoadAndWatchAsync(ModelItem item)
        {
            var mgr = LlamaManager.Shared;
            var queue = DispatcherQueue;

            item.LoadFraction = 0;
            // Progress<T> captures the UI thread's SynchronizationContext at
            // construction, so reports land on the UI thread unaided. Throttle
            // to ~10 updates/sec like the download ring (the server can stream
            // a status_change per mmap chunk); the terminal 100% always lands.
            long lastApplyMs = 0;
            var progress = new Progress<double>(f =>
            {
                var now = Environment.TickCount64;
                if (f < 1.0 && now - lastApplyMs < 100) return;
                lastApplyMs = now;
                item.LoadFraction = f;
            });

            bool ok;
            try
            {
                ok = await mgr.LoadModelAsync(item, progress);
            }
            catch (Exception ex)
            {
                // The server dying mid-load faults the SSE watch with an
                // IOException; LoadModelAsync already maps that to false, but
                // don't let anything else escape this fire-and-forget call
                // either — a stuck IsLoading would spin the row's ring forever.
                Log.Warn(ex, "load watch threw");
                ok = false;
            }
            if (!ok)
            {
                void Rejected()
                {
                    item.IsLoading = false;
                    item.LoadFraction = 0;
                    // Surface the failure: without this the ring just vanished
                    // and the play glyph returned with no explanation (OOM,
                    // corrupt GGUF, server refusal all looked identical).
                    item.LoadFailed = true;
                    NotifyWhenHidden("Couldn't load model",
                        $"{item.DisplayName} couldn't be loaded. It may not fit in memory, or the file may be corrupt.");
                }
                if (queue is null || queue.HasThreadAccess)
                    Rejected();
                else
                    queue.TryEnqueue(Rejected);
                return;
            }

            // Accepted. The poller will flip IsLoaded=true / IsLoading=false once
            // the server reports the model resident. Watchdog: if the server never
            // reports loaded within a generous window (a large model can take a
            // while to mmap), give up on the spinner so the row falls back to the
            // play glyph and stays retryable rather than spinning forever.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(2));
                if (item.IsLoading && !item.IsLoaded)
                {
                    void GiveUp()
                    {
                        if (!item.IsLoading || item.IsLoaded) return;
                        item.IsLoading = false;
                        // Same contract as a rejection: say we gave up rather
                        // than silently dropping the spinner.
                        item.LoadFailed = true;
                        NotifyWhenHidden("Load timed out",
                            $"{item.DisplayName} didn't finish loading within 2 minutes.");
                    }
                    if (queue is null || queue.HasThreadAccess)
                        GiveUp();
                    else
                        queue.TryEnqueue(GiveUp);
                }
            });
        }

        // ---- Version footer ----

        /// <summary>
        /// Fills the footer version line: the app's assembly version (no name)
        /// and the resolved llama.cpp version, separated by " - ". Only the
        /// version strings are shown, centered and bold white.
        /// </summary>
        private void LoadVersionInfo()
        {
            var appVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
            VersionText.Text = LlamaRunner.Version is { } v
                ? $"{appVer} - {v}"
                : appVer;
        }

        /// <summary>
        /// Re-renders the footer's server-status dot and relaunch button from
        /// <see cref="LlamaManager.ServerStatus"/> (mapping rules live in
        /// <see cref="ServerStatusPresentation"/> so they stay unit-testable).
        /// Called on every <see cref="LlamaManager.StateChanged"/> and once at
        /// startup.
        /// </summary>
        private void UpdateServerStatusUI()
        {
            var d = ServerStatusPresentation.Describe(
                LlamaManager.Shared.ServerStatus, LlamaManager.Shared.State,
                LlamaManager.Shared.FailureMessage);
            ServerStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(d.Dot);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ServerStatusDot, d.ToolTip);
            ServerRestartButton.Visibility = d.CanRelaunch
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        /// <summary>
        /// Relaunch button (footer, visible only when the server is down):
        /// re-runs the full ensure pipeline — adopt a server if one reappeared,
        /// otherwise resolve/install the binary and launch it. Single-flighted
        /// inside <see cref="LlamaManager.EnsureLlamaOrDownloadAsync"/>, so a
        /// double-click can't spawn two servers.
        /// </summary>
        private void ServerRestart_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Log.Info("manual server relaunch requested");
            _ = RelaunchServerAsync();

            static async Task RelaunchServerAsync()
            {
                try
                {
                    await LlamaManager.Shared.EnsureLlamaOrDownloadAsync();
                }
                catch (Exception ex)
                {
                    // Fire-and-forget from the button; the pipeline already
                    // surfaces failures via the Failed state + red dot.
                    Log.Error(ex, "manual server relaunch threw");
                }
            }
        }

        /// <summary>
        /// Handler for <see cref="LlamaManager.StateChanged"/>: re-renders the
        /// footer's llama.cpp half as the binary is detected/installed so the
        /// running llama.cpp version appears live.
        /// </summary>
        private void LlamaManager_StateChanged(object? sender, EventArgs e)
        {
            // StateChanged can fire off the UI thread (the server process Exited
            // handler runs on a thread-pool thread), so marshal before touching
            // any UI element / the LocalModels collection.
            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess)
                OnStateChanged();
            else
                dq.TryEnqueue(OnStateChanged);

            void OnStateChanged()
            {
                LoadVersionInfo();
                UpdateServerStatusUI();

                // A crash used to surface only as the footer's 8px dot colour
                // — toast the reason (LlamaManager.FailureMessage) once per
                // transition into Failed so it isn't missed while hidden.
                var serverStatus = LlamaManager.Shared.ServerStatus;
                if (serverStatus == LlamaManager.ServerState.Failed &&
                    _lastServerStatus != LlamaManager.ServerState.Failed)
                {
                    NotifyWhenHidden("Llama server stopped",
                        LlamaManager.Shared.FailureMessage
                            ?? "The llama server stopped responding.");
                }
                _lastServerStatus = serverStatus;

                // A dead server takes every in-flight operation with it —
                // downloads die mid-stream, loads never complete, loaded
                // models are gone (a restarted server comes back empty) — and
                // the poller that normally owns these flags stops while the
                // server is down. Reset all transient row state here so no row
                // keeps a ring (or a stale "open" glyph) until the server is
                // relaunched. App-driven downloads are left to their driver:
                // the dead SSE stream faults DownloadModelAsync and
                // DownloadAndLaunchAsync's catch-all flips the row to its
                // failed state with a toast.
                if (LlamaManager.Shared.ServerStatus != LlamaManager.ServerState.Running)
                {
                    foreach (var row in LocalModels)
                    {
                        row.IsLoaded = false;
                        row.IsLoading = false;
                        row.LoadFraction = 0;
                        if (row.DownloadCancellation is null)
                        {
                            row.IsDownloading = false;
                            row.DownloadFraction = 0;
                        }
                    }
                    // External-download watches die with the server too;
                    // cancel them promptly rather than waiting for each dead
                    // stream to fault on its own.
                    foreach (var cts in _externalDownloadWatches.Values)
                        cts.Cancel();
                    _externalDownloadWatches.Clear();
                }

                // Keep the empty-state text in step with the server state
                // ("Starting the llama server…" → "No models yet — …").
                UpdateEmptyState();

                // (Re)populate the Available list once the server is actually
                // running — covers the startup race where the initial fetch ran
                // before the server was ready (or /models was momentarily empty).
                // Only triggers while the list is empty, so an in-flight download
                // row is never clobbered.
                if (LlamaManager.Shared.ServerStatus == LlamaManager.ServerState.Running &&
                    LocalModels.Count == 0)
                {
                    _ = LoadLocalModelsAsync();
                }
            }
        }

        /// <summary>
        /// Handler for <see cref="LlamaManager.ModelsChanged"/> (the 1s poller):
        /// marshals the fresh server snapshot to the UI thread and reconciles it
        /// into the Available rows in place. The poller fires on a background
        /// thread, so we never touch the ObservableCollection directly here.
        /// </summary>
        private void LlamaManager_ModelsChanged(object? sender, IReadOnlyList<LlamaManager.ServerModel> models)
        {
            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess)
                _ = ReconcileAsync(models);
            else
                dq.TryEnqueue(() => _ = ReconcileAsync(models));
        }

        /// <summary>
        /// Merges a fresh <c>GET /models</c> snapshot into <see cref="LocalModels"/>
        /// without rebuilding the list (which would flicker and lose click/load
        /// state). Existing rows get their <see cref="ModelItem.IsLoaded"/>/
        /// <see cref="ModelItem.IsLoading"/> flipped to match the server's
        /// reported status; models the server now knows about that we haven't
        /// listed yet (e.g. added to the cache out-of-band) are appended with
        /// catalog enrichment. Never clears rows — a transient empty/error
        /// snapshot is a no-op, so a network blip doesn't unload the list.
        /// </summary>
        private async Task ReconcileAsync(IReadOnlyList<LlamaManager.ServerModel> serverModels)
        {
            // If the initial populate hasn't run yet, let LoadLocalModelsAsync
            // build the list (and the index) once — reconcile only updates
            // existing rows. Avoid racing the first populate.
            if (LocalModels.Count == 0)
            {
                if (Interlocked.CompareExchange(ref _loadingLocalModels, 0, 0) == 0)
                    _ = LoadLocalModelsAsync();
                return;
            }

            var byRepo = await GetCatalogByRepoAsync();

            foreach (var sm in serverModels)
            {
                if (!_localByServerId.TryGetValue(sm.Id, out var item) &&
                    (item = FindLocalByRepo(sm.Id)) is not null)
                {
                    // Exact-match miss, but a row for the same repo exists: the
                    // server ids a mid-download model by its bare repo (the quant
                    // is resolved only once the download completes), while a row
                    // moved from Recommended is keyed repo:catalogQuant. Adopt
                    // the server's id — adding a row here would show the model
                    // twice for the whole download (and leave a stale row after).
                    AdoptServerId(item, sm.Id);
                }

                if (item is not null)
                {
                    // Map the server's four model states onto the row:
                    //   loaded     -> OpenInNewWindow glyph (IsLoaded, ring off)
                    //   loading    -> load ring (server-truth load)
                    //   downloading-> download ring (server-truth download; stays
                    //                 indeterminate for externally-triggered
                    //                 downloads — no byte progress is tracked)
                    //   unloaded   -> play glyph (but don't clobber an optimistic
                    //                 IsLoading set by a just-fired play click that
                    //                 the server hasn't acknowledged yet)
                    if (sm.IsLoaded)
                    {
                        if (!item.IsLoaded)
                        {
                            Log.Info("model loaded: " + sm.Id);
                            NotifyWhenHidden("Model ready",
                                $"{item.DisplayName} is loaded and ready to chat.");
                        }
                        item.IsLoaded = true;
                        item.IsLoading = false;
                        item.IsDownloading = false;
                        item.LoadFailed = false; // server-truth loaded clears any stale failure
                        StopExternalDownloadWatch(item);
                    }
                    else if (sm.IsDownloading)
                    {
                        if (!item.IsDownloading) Log.Info("model downloading: " + sm.Id);
                        item.IsLoaded = false;
                        item.IsLoading = false;
                        item.IsDownloading = true;
                        // A download the app didn't start (WebUI/CLI) has no
                        // driver wiring byte progress — watch it over SSE
                        // ourselves, or the row sits on the indeterminate ring
                        // for the whole download.
                        if (item.DownloadCancellation is null)
                            EnsureExternalDownloadWatch(item, sm.Id);
                    }
                    else if (sm.IsLoading)
                    {
                        if (!item.IsLoading) Log.Info("model loading: " + sm.Id);
                        item.IsLoaded = false;
                        item.IsLoading = true;
                        item.IsDownloading = false;
                        StopExternalDownloadWatch(item);
                    }
                    else // "unloaded" (or unknown)
                    {
                        if (item.IsLoaded) Log.Info("model unloaded: " + sm.Id);
                        item.IsLoaded = false;
                        // Clear IsDownloading only for downloads the poller owns
                        // (externally triggered ones): an app-driven download's
                        // driver (DownloadAndLaunchAsync) flips the row to loading
                        // itself — clearing here first would bounce the row back
                        // to the play glyph for up to one poll cycle.
                        if (item.DownloadCancellation is null)
                        {
                            item.IsDownloading = false;
                            StopExternalDownloadWatch(item);
                        }
                        // Leave IsLoading alone: a just-fired play click sets it
                        // optimistically before the server transitions to "loading";
                        // clearing it here would flicker the ring off for up to one
                        // poll cycle. Once the server reports "loading" or "loaded"
                        // the branches above take over.
                    }
                }
                else
                {
                    // New server model not yet listed — add an enriched row.
                    var newItem = BuildLocalItem(sm, byRepo);
                    _localByServerId[sm.Id] = newItem;
                    LocalModels.Add(newItem);
                    Log.Info("added new local row from poller: " + sm.Id);
                }
            }

            // Sweep poller-owned download rows whose model vanished from
            // /models entirely: an externally canceled (or failed) download
            // disappears from the list, and without this the row would keep its
            // ring — and its progress watch — forever. App-driven downloads are
            // owned by their driver (DownloadAndLaunchAsync) and never touched.
            var serverIds = new HashSet<string>(
                serverModels.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, row) in _localByServerId)
            {
                if (row.IsDownloading && row.DownloadCancellation is null &&
                    !serverIds.Contains(key))
                {
                    Log.Info("download vanished from /models: " + key);
                    StopExternalDownloadWatch(row);
                    row.IsDownloading = false;
                    row.DownloadFraction = 0;
                }
            }

            UpdateEmptyState();
        }

        /// <summary>
        /// Starts (once per row) an SSE progress watch for a download the app did
        /// not start itself. The watch only feeds <see cref="ModelItem.DownloadFraction"/>;
        /// state transitions stay with the poller. It stops by itself when the
        /// download finishes or fails, and is canceled via
        /// <see cref="StopExternalDownloadWatch"/> when the row leaves the
        /// downloading state.
        /// </summary>
        private void EnsureExternalDownloadWatch(ModelItem item, string serverId)
        {
            if (_externalDownloadWatches.ContainsKey(item))
                return;

            // Mid-download the server ids the model by its bare repo, which is
            // also what the SSE "model" field carries.
            var repo = SplitServerId(serverId).repo;
            var cts = new CancellationTokenSource();
            _externalDownloadWatches[item] = cts;
            Log.Info("watching external download: " + repo);
            _ = WatchExternalDownloadAsync(item, repo, cts);
        }

        /// <summary>
        /// Cancels and forgets a row's external-download progress watch, if any.
        /// The token source is not disposed here — the watcher task disposes it
        /// itself when it unwinds, so it can never observe a disposed source.
        /// </summary>
        private void StopExternalDownloadWatch(ModelItem item)
        {
            if (_externalDownloadWatches.Remove(item, out var cts))
                cts.Cancel();
        }

        private async Task WatchExternalDownloadAsync(ModelItem item, string repo, CancellationTokenSource cts)
        {
            long lastApplyMs = 0;
            long lastSampleBytes = 0, lastSampleMs = 0;
            double bytesPerSecond = 0;
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                // Same throttle as DownloadAndLaunchAsync: ~10 UI updates/s, and
                // total==0 events (stream noise) never touch the fraction.
                var now = Environment.TickCount64;
                if (!p.Done && now - lastApplyMs < 100) return;
                lastApplyMs = now;

                // Same speed estimate as the app-driven path, so an external
                // download's row shows the same detail line.
                if (p.DownloadedBytes > 0)
                {
                    if (lastSampleMs != 0 && p.DownloadedBytes > lastSampleBytes)
                    {
                        var instantaneous = (p.DownloadedBytes - lastSampleBytes)
                            * 1000.0 / Math.Max(1, now - lastSampleMs);
                        bytesPerSecond = DownloadProgressPresentation
                            .SmoothSpeed(bytesPerSecond, instantaneous);
                    }
                    lastSampleBytes = p.DownloadedBytes;
                    lastSampleMs = now;
                }

                if (p.TotalBytes > 0)
                {
                    item.DownloadFraction = p.Fraction;
                    item.DownloadedBytes = p.DownloadedBytes;
                    item.DownloadTotalBytes = p.TotalBytes;
                    item.DownloadBytesPerSecond = bytesPerSecond;
                }
            });

            try
            {
                await LlamaManager.Shared.WatchDownloadAsync(repo, progress, cts.Token);
            }
            catch (Exception ex)
            {
                // Fire-and-forget: nothing upstream would observe a fault.
                Log.Warn(ex, "external download watch faulted: " + repo);
            }

            // The entry may already be gone — or replaced by a newer watch — if
            // the poller stopped this one first; only remove our own.
            if (_externalDownloadWatches.TryGetValue(item, out var current) &&
                ReferenceEquals(current, cts))
                _externalDownloadWatches.Remove(item);
            cts.Dispose();
        }

        /// <summary>
        /// Finds an Available row by bare repo id (the part of a server model id
        /// before <c>:</c>). The server ids a mid-download model by its bare repo
        /// — the quant is resolved only once the download completes — so an exact
        /// <see cref="_localByServerId"/> lookup misses rows that were keyed
        /// <c>repo:quant</c> (e.g. moved from Recommended on tap).
        /// </summary>
        private ModelItem? FindLocalByRepo(string serverId)
        {
            var (repo, _) = SplitServerId(serverId);
            foreach (var (key, row) in _localByServerId)
            {
                if (string.Equals(SplitServerId(key).repo, repo, StringComparison.OrdinalIgnoreCase))
                    return row;
            }
            return null;
        }

        /// <summary>
        /// Re-keys <paramref name="item"/> under the id the server is currently
        /// reporting for it, dropping any previous alias. Also adopts the server's
        /// resolved quant once the id carries one (mid-download ids are bare
        /// repos) — <c>/models/load</c> and <c>DELETE /models/{name}</c> must use
        /// the server's real id, which can differ from the catalog quant the row
        /// was tapped with.
        /// </summary>
        private void AdoptServerId(ModelItem item, string serverId)
        {
            foreach (var key in _localByServerId
                         .Where(kv => ReferenceEquals(kv.Value, item))
                         .Select(kv => kv.Key).ToList())
                _localByServerId.Remove(key);

            var (_, quant) = SplitServerId(serverId);
            if (quant.Length > 0 &&
                !string.Equals(item.Quant, quant, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"adopting server-resolved quant {quant} for {serverId} (was {item.Quant})");
                item.Quant = quant;
            }

            _localByServerId[serverId] = item;
        }

        // ---- Footer actions ----

        private async void ServerLink_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Open the running llama server's WebUI in the system browser.
            await Windows.System.Launcher.LaunchUriAsync(
                new System.Uri($"http://localhost:{LlamaManager.Shared.ServerPort}"));
        }

        private void Settings_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Hide the flyout first so the settings dialog isn't drawn behind it
            // (the flyout would otherwise immediately deactivate and hide on its
            // own, but doing it explicitly avoids a flash).
            HideFlyout();

            var w = new SettingsWindow();
            // The token may have changed — re-resolve the header avatar.
            w.Closed += (_, _) => _ = LoadAvatarAsync();
            w.Activate();
        }

        // ---- HF avatar ----

        // Profile URL the avatar button opens (hf.co/<name>). Null while no
        // whoami-v2 lookup has succeeded.
        private string? _avatarProfileUrl;

        /// <summary>
        /// Resolves the Hugging Face user behind the configured token
        /// (whoami-v2) and shows their avatar in the header, left of the
        /// settings gear. Hidden when no token is configured; a rejected token
        /// or network failure just keeps the previous state — the avatar is a
        /// best-effort decoration. Runs on the UI thread after the await.
        /// </summary>
        private async Task LoadAvatarAsync()
        {
            try
            {
                var token = Settings.Current.HuggingFaceToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    AvatarButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _avatarProfileUrl = null;
                    return;
                }

                var info = await new HubClient(token).UserInfo.WhoAmI();
                if (info is null) return; // rejected token / network hiccup — keep as-is

                if (!string.IsNullOrEmpty(info.AvatarUrl))
                {
                    AvatarPicture.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging
                        .BitmapImage(new Uri(info.AvatarUrl));
                }
                // Initials fallback if the image is missing or fails to load.
                AvatarPicture.DisplayName = info.Name;

                // The public profile page is hf.co/<username> — the whoami `id`
                // is an internal ObjectId the website doesn't route.
                _avatarProfileUrl = $"https://hf.co/{info.Name}";
                AvatarButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "avatar load failed; staying hidden");
            }
        }

        private async void Avatar_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_avatarProfileUrl is null) return;
            await Windows.System.Launcher.LaunchUriAsync(new Uri(_avatarProfileUrl));
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

            // WS_EX_TOOLWINDOW keeps the window out of the taskbar entirely.
            _hwnd = WindowNative.GetWindowHandle(this);

            // Initial size. PositionNear re-sizes with the target monitor's DPI
            // on every show, so the window's current DPI is good enough here.
            var dpi = GetDpiForWindow(_hwnd);
            AppWindow.Resize(FlyoutSizeForDpi(dpi, dpi));

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

            // Pin the corner radius to the standard 8px "round" style rather
            // than relying on the system default.
            WindowCorners.ApplyRound8(this);
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

        /// <summary>
        /// Esc dismisses the flyout — the same convention as the chat overlay
        /// (and as clicking away, which hides on deactivation). The accelerator
        /// is window-level, so it fires wherever focus sits inside the flyout.
        /// </summary>
        private void EscapeAccelerator_Invoked(
            Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            HideFlyout();
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
            // Pin the flyout to the bottom-right of the work area of the monitor
            // nearest the click — i.e. just above the taskbar, next to the tray.
            // The size is scaled by THAT monitor's DPI, so the flyout keeps the
            // same logical size whichever screen it appears on.
            var work = GetWorkArea(anchor);
            var (dpiX, dpiY) = GetMonitorDpi(anchor);
            var size = FlyoutSizeForDpi(dpiX, dpiY);

            AppWindow.Resize(size);
            AppWindow.Move(new PointInt32(work.Right - size.Width, work.Bottom - size.Height));
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

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll", ExactSpelling = true)]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const int MDT_EFFECTIVE_DPI = 0;

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

        /// <summary>
        /// Converts the flyout's DIP size to physical pixels at the given DPI.
        /// The flyout is designed in DIPs (the units XAML layout uses), but
        /// <see cref="AppWindow.Resize"/>/<see cref="AppWindow.Move"/> take
        /// physical pixels — without this scaling the flyout's logical size
        /// (how much content fits) would shrink on high-DPI screens.
        /// </summary>
        private static SizeInt32 FlyoutSizeForDpi(uint dpiX, uint dpiY) => new(
            (int)Math.Round(FlyoutWidthDips * dpiX / 96.0),
            (int)Math.Round(FlyoutHeightDips * dpiY / 96.0));

        /// <summary>
        /// Returns the effective DPI of the monitor nearest
        /// <paramref name="anchor"/>, defaulting to 96 (100% scaling) if the
        /// query fails.
        /// </summary>
        private static (uint X, uint Y) GetMonitorDpi(Point anchor)
        {
            var hmon = MonitorFromPoint(new POINT { X = anchor.X, Y = anchor.Y }, MONITOR_DEFAULTTONEAREST);
            return GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0
                ? (dpiX, dpiY)
                : (96u, 96u);
        }
    }
}
