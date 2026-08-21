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
    /// app on Windows while hosting the three-section models panel.
    /// </summary>
    public sealed partial class MainWindow : Window, IModelItemDetailsHost, IModelFamilyDetailsHost
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

        /// <summary>
        /// The rows of the single models list: installed model rows, the
        /// "Browse more" separator, then catalog family rows. Built from
        /// <see cref="LocalModels"/> + <see cref="Families"/> by
        /// <see cref="RebuildItems"/>/<see cref="RebuildBrowseTail"/>; the
        /// 1s poller never touches it (property updates flow via bindings).
        /// </summary>
        public ObservableCollection<ModelListItemViewModel> Items { get; } = [];

        /// <summary>The catalog's featured model families (browse section).</summary>
        public ObservableCollection<ModelFamilyViewModel> Families { get; } = [];

        // Wrapper cache: an installed row keeps the same list-item view-model
        // across unrelated rebuilds, so the ListView's realized containers
        // (and scroll position) survive membership churn.
        private readonly Dictionary<ModelItem, InstalledModelListItemViewModel> _installedWrappers = new();

        /// <summary>
        /// The remote catalog, fetched once as model families and shared by
        /// both sections: the browse section lists the families directly, the
        /// installed section uses the flattened form to enrich server-
        /// reported models (display name, params, size, brand logo). Resolved
        /// before the per-section loaders run.
        /// </summary>
        private Task<List<ModelFamily>> _familiesTask = null!;

        // <summary>
        // The catalog reshaped into a bare-repo-id → <see cref="Repository"/>
        // lookup, collapsing the per-quant duplicates. Built EXACTLY ONCE, as a
        // continuation over <see cref="_familiesTask"/> — the awaiters (initial
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

        /// <summary>
        /// Last-seen <see cref="LlamaManager.BinaryPath"/> — the browse
        /// families' fit evaluation needs the binary for its device probe, so
        /// a catalog that landed before the binary was resolved/installed saw
        /// no devices; the families are re-evaluated once one appears (see
        /// <c>OnStateChanged</c>).
        /// </summary>
        private string? _lastBinaryPath;

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

            // The single list mirrors LocalModels membership (installed rows)
            // plus the browse tail (separator + families). Property updates
            // (download progress, load state) flow through bindings without
            // touching the collection — the 1s poller never rebuilds rows.
            LocalModels.CollectionChanged += (_, _) =>
            {
                RebuildItems();
                RebuildBrowseTail();
            };

            LoadModels();
            LoadVersionInfo();
            UpdateServerStatusUI();
            _ = UpdateGpuIndicatorAsync();
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
        /// Populates the model list. The browse section (catalog families)
        /// comes straight from the remote catalog; the installed rows are
        /// fetched from the running llama server's <c>GET /models</c> once
        /// it's reachable. Both share a single catalog fetch (the installed
        /// rows are enriched from it).
        /// </summary>
        private void LoadModels()
        {
            StartCatalogFetch();
            _ = LoadFamiliesAsync();
            _ = LoadLocalModelsAsync();
        }

        /// <summary>
        /// (Re)fetches the remote catalog and re-projects the repo-id lookup.
        /// Split out of <see cref="LoadModels"/> so the browse section's
        /// "Try again" button can refetch without touching the installed list.
        /// </summary>
        private void StartCatalogFetch()
        {
            _familiesTask = FetchFamiliesAsync();
            // Project the fetched catalog into a repo-id lookup exactly once — a
            // continuation that runs when the fetch completes, shared by every
            // caller of GetCatalogByRepoAsync. ContinueWith(NotOnFaulted,
            // TaskScheduler.Default) so an (impossible — FetchFamiliesAsync never
            // faults) fault still yields a usable empty dictionary rather than
            // a faulted task awaited by callers that don't expect a throw.
            _catalogByRepoTask = _familiesTask.ContinueWith(
                t => Catalog.Flatten(t.Result)
                    .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Fetches the remote catalog once as model families for both
        /// sections. Never throws — a network failure just yields an empty
        /// list (the browse section shows its inline error, installed rows
        /// aren't enriched).
        /// </summary>
        private async Task<List<ModelFamily>> FetchFamiliesAsync()
        {
            try { return (await Catalog.FetchFamiliesAsync()).ToList(); }
            catch (Exception ex)
            {
                Log.Warn(ex, "catalog fetch failed; browse section stays empty");
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
        /// <see cref="_familiesTask"/> in <see cref="LoadModels"/>; callers just
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
                Name = DeriveDisplayName(repo, quant, byRepo),
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
        /// <c>DisplayName</c> with the quant in parens when known, else the last
        /// path segment of the repo id (with quant in parens).
        /// </summary>
        private static string DeriveDisplayName(
            string repo, string quant, Dictionary<string, Repository> byRepo)
        {
            byRepo.TryGetValue(repo, out var matched);
            var baseName = !string.IsNullOrEmpty(matched?.DisplayName)
                ? matched.DisplayName
                : repo.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? repo;
            return string.IsNullOrEmpty(quant) ? baseName : $"{baseName} ({quant})";
        }

        /// <summary>
        /// Loads the catalog's model families into the browse section of the
        /// single list. Shares the single catalog fetch with the installed
        /// section. Async and non-blocking: while the fetch is in flight the
        /// status line below the list says so, and installed models stay
        /// fully usable.
        /// </summary>
        private async Task LoadFamiliesAsync()
        {
            List<ModelFamily> families;
            try { families = await _familiesTask; }
            catch (Exception ex) { Log.Warn(ex, "model families load failed"); families = []; }

            // An empty catalog after the fetch means it couldn't be loaded
            // (network/parse failure) — say so and offer a retry instead of
            // leaving the section silently blank.
            if (families.Count == 0)
            {
                BrowseStatusRing.IsActive = false;
                BrowseStatusRing.Visibility = Visibility.Collapsed;
                BrowseStatusText.Text = "Couldn't load the model catalog. Check your connection and try again.";
                RetryCatalogButton.Visibility = Visibility.Visible;
                BrowseStatusPanel.Visibility = Visibility.Visible;
                RebuildBrowseTail();
                return;
            }

            Families.Clear(); // idempotent — safe on catalog retry

            // Only featured families are shown, in catalog order (the
            // catalog's own ordering is the deterministic display policy —
            // no quality ranking or provider endorsement).
            foreach (var family in RecommendedFiltering.FilterFamiliesForDisplay(families))
                Families.Add(new ModelFamilyViewModel(family, ModelItem.ResolveLogo));

            BrowseStatusPanel.Visibility = Visibility.Collapsed;
            RebuildBrowseTail();

            // Dim the families the machine can't run — probe the devices
            // once (`llama --list-devices`, CPU/RAM fallback) and check
            // every build's estimated footprint against them. Fire-and-
            // forget: the probe spawns a process and the list should render
            // instantly; rows update in place once the verdicts land.
            _ = EvaluateFamilyFitsAsync();
        }

        /// <summary>
        /// Single-flight guard for <see cref="EvaluateFamilyFitsAsync"/>
        /// — catalog retry and the binary-appearance re-evaluation can race.
        /// </summary>
        private bool _fitEvaluationRunning;

        /// <summary>
        /// Dims the browse families whose every downloadable build is
        /// estimated not to fit this machine — and sinks them below the
        /// fitting families, so the top of the browse section is always
        /// what this machine can run (a family fits when ANY of its builds
        /// fits — the details view lets the user pick a smaller size/quant).
        /// Probes the accelerator devices once via
        /// <see cref="LlamaManager.ListDevicesAsync"/> (system RAM when none)
        /// and runs each build's catalog metadata (params/quant/size) through
        /// <see cref="ModelMemoryEstimator"/> + <see cref="MemoryFit"/> — the
        /// same math the download preflight uses, so a dimmed family and a
        /// blocked download always agree. Unknown estimates leave the family
        /// at full strength (fail open).
        /// </summary>
        private async Task EvaluateFamilyFitsAsync()
        {
            if (_fitEvaluationRunning) return;
            _fitEvaluationRunning = true;
            try
            {
                var devices = await LlamaManager.Shared.ListDevicesAsync();
                var availableRam = LlamaApp.Llama.SystemMemory.TryGet(out _, out var avail) ? (ulong?)avail : null;

                // Snapshot the rows: a catalog retry can repopulate the list
                // mid-run — updating stale items is harmless, and the fresh
                // pass covers the new ones.
                var rows = Families.ToList();
                foreach (var family in rows)
                {
                    // The note quotes the least-demanding build that still
                    // didn't fit — the family's best shot — so the tooltip
                    // reads honest ("even the smallest doesn't fit").
                    MemoryFitResult? bestFit = null;
                    foreach (var size in family.Family.Sizes)
                    {
                        foreach (var build in size.Builds)
                        {
                            var estimate = ModelMemoryEstimator.Estimate(
                                size.Params, build.Quant, build.SizeBytes);
                            var fit = MemoryFit.Check(estimate, devices, availableRam);
                            if (fit.Fits)
                            {
                                bestFit = fit;
                                break;
                            }
                            if (bestFit is null || fit.RequiredBytes < bestFit.RequiredBytes)
                                bestFit = fit;
                        }
                        if (bestFit is { Fits: true }) break;
                    }

                    // Note first, then the flag: the RowToolTip notification
                    // from FitsOnDevice already carries the reason.
                    family.FitNote = bestFit is { Fits: true } ? null : DescribeFitFailure(bestFit!);
                    family.FitsOnDevice = bestFit is not { Fits: false };
                }

                // Fitting families on top, the rest sink below them (still
                // rendered, still dimmed — catalog order is preserved within
                // each half). Guarded against the stale-snapshot race: if a
                // retry repopulated the list mid-run, its own pass owns the
                // ordering.
                if (Families.Count == rows.Count &&
                    rows.All(Families.Contains))
                {
                    RecommendedFiltering.ApplyOrder(
                        Families,
                        RecommendedFiltering.PartitionFitFirst(rows, f => f.FitsOnDevice));
                    RebuildBrowseTail();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fit graying is a hint — a failed probe just leaves every
                // family at full strength (the download preflight still guards).
                Log.Warn(ex, "family fit evaluation failed");
            }
            finally
            {
                _fitEvaluationRunning = false;
            }
        }

        /// <summary>
        /// The dimmed family's tooltip line: what even its smallest build
        /// needs versus what the machine has. Wording mirrors
        /// <see cref="ShowInsufficientMemoryFlyout"/> so the dimmed hint and
        /// the click-time flyout tell the same story.
        /// </summary>
        private static string DescribeFitFailure(MemoryFitResult fit)
        {
            var required = MemoryFit.FormatBytes(fit.RequiredBytes);
            return fit.Devices.Count > 0
                ? $"May not fit: needs about {required}, more than the free memory on " +
                  $"{DescribeDevices(fit.Devices)} and the usable system memory."
                : $"May not fit: needs about {required}, but only " +
                  $"{MemoryFit.FormatBytes(fit.AvailableBytes)} of system memory is usable.";
        }

        /// <summary>
        /// "Try again" shown when the catalog fetch fails: refetches the
        /// catalog and repopulates the browse section. The installed list is
        /// left alone — it comes from the running server, not the catalog.
        /// </summary>
        private void RetryCatalog_Click(object sender, RoutedEventArgs e)
        {
            BrowseStatusRing.IsActive = true;
            BrowseStatusRing.Visibility = Visibility.Visible;
            BrowseStatusText.Text = "Loading models…";
            RetryCatalogButton.Visibility = Visibility.Collapsed;
            StartCatalogFetch();
            _ = LoadFamiliesAsync();
        }

        /// <summary>
        /// Re-syncs the list's installed prefix with <see cref="LocalModels"/>:
        /// wrappers are cached per model so an unchanged row keeps its
        /// view-model (and the ListView its realized container) across
        /// unrelated rebuilds. Never touches the browse tail.
        /// </summary>
        private void RebuildItems()
        {
            // Drop wrappers whose model left the collection.
            var current = new HashSet<ModelItem>(LocalModels);
            foreach (var model in _installedWrappers.Keys.Where(m => !current.Contains(m)).ToList())
            {
                Items.Remove(_installedWrappers[model]);
                _installedWrappers.Remove(model);
            }

            // Ensure every local model has a wrapper at its position.
            for (var i = 0; i < LocalModels.Count; i++)
            {
                var model = LocalModels[i];
                if (!_installedWrappers.TryGetValue(model, out var wrapper))
                {
                    wrapper = new InstalledModelListItemViewModel(model);
                    _installedWrappers[model] = wrapper;
                }
                var index = Items.IndexOf(wrapper);
                if (index < 0)
                    Items.Insert(Math.Min(i, Items.Count), wrapper);
                else if (index != i)
                {
                    Items.RemoveAt(index);
                    Items.Insert(i, wrapper);
                }
            }

            UpdateRowDividers();
        }

        /// <summary>
        /// Rebuilds the list's browse tail (everything after the installed
        /// prefix): the "Browse more" separator (only when there is something
        /// on both sides of it) plus the catalog family rows. The
        /// loading/error status line lives below the ListView, not in it.
        /// </summary>
        private void RebuildBrowseTail()
        {
            for (var i = Items.Count - 1; i >= LocalModels.Count; i--)
                Items.RemoveAt(i);

            if (Families.Count > 0)
            {
                if (LocalModels.Count > 0)
                    Items.Add(new BrowseSeparatorListItemViewModel());
                foreach (var family in Families)
                    Items.Add(new ModelFamilyListItemViewModel(family));
            }

            UpdateRowDividers();
        }

        /// <summary>
        /// Toggles the subtle row dividers: between content rows only —
        /// never adjacent to the "Browse more" separator, never on the last
        /// row.
        /// </summary>
        private void UpdateRowDividers()
        {
            ModelListItemViewModel? previous = null;
            foreach (var item in Items)
            {
                item.ShowDivider = previous is not null
                    && previous.Kind != ModelListItemKind.BrowseSeparator
                    && item.Kind != ModelListItemKind.BrowseSeparator;
                previous = item;
            }
        }

        /// <summary>
        /// Shows/hides the "No model yet." placeholder based on whether any
        /// local models are present.
        /// </summary>
        private void UpdateEmptyState()
        {
            var empty = LocalModels.Count == 0;
            NoLocalModelsText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
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
        /// The one-tap catalog download, shared by the family details view's
        /// variant rows (and the installed-details view's Download action):
        /// disk-space and memory preflights, then the model moves into the
        /// installed section (downloading) and <see cref="DownloadAndLaunchAsync"/>
        /// drives it. The family row stays in the browse list — other
        /// sizes/variants of the family remain installable.
        /// </summary>
        private async Task StartRecommendedDownloadAsync(ModelItem item, FrameworkElement spaceFlyoutTarget)
        {
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
                ShowInsufficientSpaceFlyout(spaceFlyoutTarget, item, freeBytes);
                return;
            }

            // Memory preflight: query `llama --list-devices` for the free
            // VRAM (falling back to system RAM when there are no devices)
            // and estimate the model's footprint from its params/quant/size.
            // Block a multi-GB download the machine can't run — unless the
            // probe or the estimate came back unknown (fail open, same as
            // the disk check above).
            try
            {
                var fit = await LlamaManager.Shared.CheckModelFitAsync(
                    item.Parameters, item.Quant, item.SizeBytes);
                if (!fit.Fits)
                {
                    Log.Info($"download blocked: {((IModel)item).ServerModelId} needs " +
                        $"{fit.RequiredBytes} bytes; {fit.Details}");
                    ShowInsufficientMemoryFlyout(spaceFlyoutTarget, item, fit);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn(ex, "memory preflight failed; allowing the download");
            }

            // Move the model into the installed section (downloading).
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
        /// Shows a light-dismiss flyout on the tapped Recommended row when the
        /// memory preflight blocks a download (the model wouldn't fit in the
        /// free VRAM or system RAM). Same flyout-not-dialog rationale as
        /// <see cref="ShowInsufficientSpaceFlyout"/>.
        /// </summary>
        private static void ShowInsufficientMemoryFlyout(FrameworkElement target, ModelItem item, MemoryFitResult fit)
        {
            var requiredText = MemoryFit.FormatBytes(fit.RequiredBytes);

            var body = fit.Devices.Count > 0
                ? $"{item.DisplayName} needs about {requiredText}, but the free memory on " +
                  $"{DescribeDevices(fit.Devices)} isn't enough (and neither is the free " +
                  "system memory). Try a smaller model or quant."
                : $"{item.DisplayName} needs about {requiredText}, but only " +
                  $"{MemoryFit.FormatBytes(fit.AvailableBytes)} of system memory is usable. " +
                  "Try a smaller model or quant.";

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
                            Text = "Not enough memory",
                            FontSize = 13,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = body,
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
        /// Describes the probed devices for the memory flyout, e.g.
        /// "NVIDIA GeForce RTX 4060 Ti (14.1 GB free)" or the joined list for
        /// multi-GPU machines.
        /// </summary>
        private static string DescribeDevices(IReadOnlyList<LlamaDevice> devices) =>
            DeviceStatusPresentation.DescribeDeviceList(devices);

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
            // source. DownloadModelAsync closes the SSE stream when the token
            // fires; the catch below then tells the server to stop the
            // download (pause keeps the partials, cancel discards them).
            using var cts = new CancellationTokenSource();
            item.DownloadCancellation = cts;
            // Reset any stale detail from a previous (failed or paused) attempt
            // — the subtitle shows the live detail line as soon as a size is
            // known. Fresh SSE progress events repopulate the byte counts.
            item.DownloadPaused = false;
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
                    // A pause click that raced the completion is discarded —
                    // the download is over, there is nothing left to resume.
                    item.DownloadPaused = false;
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
                // User canceled from the row's cancel button, or paused by
                // clicking the ring — the SSE stream is closed (see
                // DownloadModelAsync), but the server-side download is still
                // running: tell the server to stop it. A pause (DownloadPaused
                // set by the click) unloads the download child and keeps the
                // partial bytes so a resume continues where it left off; a
                // cancel deletes them (the next attempt starts from zero).
                // Awaited BEFORE the row leaves the downloading state: both
                // server endpoints return once the teardown is done, so the
                // poller can't observe a still-downloading model afterwards
                // and bounce the row back onto the ring.
                if (item.DownloadPaused)
                    await mgr.PauseServerDownloadAsync(((IModel)item).Name);
                else
                    await mgr.CancelServerDownloadAsync(((IModel)item).Name);

                // A cancel returns the row to the play glyph; a pause lands it
                // on the resume glyph instead.
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
                    item.DownloadPaused = false;
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
        /// (see <see cref="DownloadAndLaunchAsync"/>), which discards the
        /// partial bytes — the next attempt starts from zero. While paused the
        /// button abandons the partial instead: the server side is already
        /// stopped, so all that's left is deleting the leftover bytes.
        /// </summary>
        private void LocalModelCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }

            if (item.DownloadPaused)
            {
                // Paused: the server-side download already stopped when the
                // pause was requested — abandon the partial and return the row
                // to the play glyph. The DELETE also discards the leftover
                // bytes in the cache (best-effort: if the server already
                // dropped the transient entry the request 404s and the bytes
                // simply resume on the next attempt).
                Log.Info("cancel clicked: abandoning paused download of " + ((IModel)item).ServerModelId);
                item.DownloadPaused = false;
                item.DownloadFraction = 0;
                item.DownloadedBytes = 0;
                item.DownloadTotalBytes = 0;
                item.DownloadBytesPerSecond = 0;
                _ = LlamaManager.Shared.CancelServerDownloadAsync(((IModel)item).Name);
                return;
            }

            Log.Info("cancel clicked: cancelling download of " + ((IModel)item).ServerModelId);
            try { item.DownloadCancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* download finished between check and click */ }
        }

        /// <summary>
        /// Fired when the download progress ring is tapped: pauses the
        /// download. Pause reuses the cancel path — the cancellation unwinds
        /// <see cref="DownloadAndLaunchAsync"/>, which asks the server to stop
        /// the download child WITHOUT deleting the partial bytes
        /// (<see cref="LlamaManager.PauseServerDownloadAsync"/>) — but with
        /// <see cref="ModelItem.DownloadPaused"/> set first, so the row lands
        /// on the resume glyph instead of the play glyph. Resuming continues
        /// where it left off. No-op for externally-triggered downloads (the
        /// ring's button is disabled then — see <see cref="ModelItem.CanPauseDownload"/>).
        /// </summary>
        private void LocalModelPauseDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }
            if (!item.IsDownloading || item.DownloadCancellation is null)
                return;

            Log.Info("pause clicked: pausing download of " + ((IModel)item).ServerModelId);
            item.DownloadPaused = true;
            try { item.DownloadCancellation.Cancel(); }
            catch (ObjectDisposedException) { /* download finished between check and click */ }
        }

        /// <summary>
        /// Fired when the resume glyph on a paused row is tapped: restarts the
        /// download → load lifecycle. The server resumes the transfer from the
        /// partial bytes left in the cache by the pause's abort.
        /// </summary>
        private void LocalModelResumeDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || ResolveRowItem(fe) is not { } item)
            {
                Log.Warn("could not resolve a ModelItem");
                return;
            }
            if (!item.DownloadPaused || item.IsDownloading)
                return;

            Log.Info("resume clicked: resuming download of " + ((IModel)item).ServerModelId);
            item.DownloadPaused = false;
            item.IsDownloading = true;
            _ = DownloadAndLaunchAsync(item);
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

            await DeleteModelFromServerAsync(item);
        }

        /// <summary>
        /// The confirmed delete, shared by the row's flyout and the details
        /// view: asks the server to remove the model and, on success, drops
        /// the row immediately (the poller's next tick would drop it too, but
        /// removing now avoids a stale row lingering for up to one poll
        /// interval). Returns whether the model was deleted.
        /// </summary>
        private async Task<bool> DeleteModelFromServerAsync(ModelItem item)
        {
            Log.Info("delete confirmed: removing " + ((IModel)item).ServerModelId);
            if (await LlamaManager.Shared.DeleteModelAsync(item))
            {
                _localByServerId.Remove(((IModel)item).ServerModelId);
                LocalModels.Remove(item);
                UpdateEmptyState();
                return true;
            }

            Log.Warn("server rejected delete for " + ((IModel)item).ServerModelId);
            return false;
        }

        // ----- Model details view -----

        /// <summary>The details ViewModel currently shown; null when the list is showing.</summary>
        private ModelItemDetailsViewModel? _detailsViewModel;

        /// <summary>Row body of an installed row: open the model details view.</summary>
        private void LocalModelDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && ResolveRowItem(fe) is { } item)
                ShowDetails(item);
        }

        /// <summary>A family row: open the family details view.</summary>
        private void ModelFamily_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ModelFamilyViewModel family)
                ShowFamilyDetails(family);
        }

        /// <summary>
        /// Swaps the models list for the details view of <paramref name="item"/>.
        /// The ViewModel is created per show (cheap) and loads its lazy details
        /// (the GGUF header read) after the view is already visible; the list's
        /// scroll position is untouched, so Back returns exactly where the user
        /// left. The context-length preference delegates wrap
        /// <see cref="Settings.ModelContextLengths"/> — per-model, persisted.
        /// </summary>
        private void ShowDetails(ModelItem item)
        {
            DisposeDetails();
            var vm = new ModelItemDetailsViewModel(
                item,
                host: this,
                loadContextPreference: id =>
                    Settings.Current.ModelContextLengths.TryGetValue(id, out var t) ? t : null,
                saveContextPreference: (id, t) =>
                {
                    Settings.Current.ModelContextLengths[id] = t;
                    Settings.Current.Save();
                },
                // The fit-params refinement awaits CLI processes; its verdicts
                // can land on a thread-pool thread — flip bound properties on
                // the UI thread.
                dispatchToUi: action => DispatcherQueue?.TryEnqueue(() => action()));
            _detailsViewModel = vm;
            DetailsView.SetViewModel(vm);
            ModelsPanel.Visibility = Visibility.Collapsed;
            FamilyDetailsView.Visibility = Visibility.Collapsed;
            DetailsView.Visibility = Visibility.Visible;
            DetailsView.FocusFirst();
            _ = vm.InitializeAsync();
        }

        /// <summary>The family details ViewModel currently shown; null when the list is showing.</summary>
        private ModelFamilyDetailsViewModel? _familyDetailsViewModel;

        /// <summary>
        /// Swaps the models list for the details view of a catalog family —
        /// the size → variant → download hierarchy. The ViewModel is created
        /// per show (cheap: everything comes from the fetched catalog).
        /// </summary>
        private void ShowFamilyDetails(ModelFamilyViewModel family)
        {
            DisposeDetails(); // the two details views are mutually exclusive
            var vm = new ModelFamilyDetailsViewModel(family.Family, host: this,
                // Same math the browse-list dimming and the download preflight
                // use (estimator + device/RAM probe), so a dimmed variant,
                // a dimmed family row, and a blocked download always agree.
                variantFitProbe: async (size, build, token) =>
                {
                    var fit = await LlamaManager.Shared.CheckModelFitAsync(
                        size.Params, build.Quant, build.SizeBytes, token);
                    return new VariantFitVerdict(fit.Fits, fit.Fits ? null : DescribeFitFailure(fit));
                });
            _familyDetailsViewModel = vm;
            FamilyDetailsView.SetViewModel(vm);
            ModelsPanel.Visibility = Visibility.Collapsed;
            DetailsView.Visibility = Visibility.Collapsed;
            FamilyDetailsView.Visibility = Visibility.Visible;
            FamilyDetailsView.FocusFirst();
        }

        /// <summary>Back row in the details view: return to the models list.</summary>
        private void DetailsView_BackRequested(object? sender, EventArgs e) => HideDetails();

        /// <summary>Back row in the family details view: return to the models list.</summary>
        private void FamilyDetailsView_BackRequested(object? sender, EventArgs e) => HideDetails();

        /// <summary>Swaps the details view back for the models list.</summary>
        private void HideDetails()
        {
            DisposeDetails();
            DetailsView.Visibility = Visibility.Collapsed;
            FamilyDetailsView.Visibility = Visibility.Collapsed;
            ModelsPanel.Visibility = Visibility.Visible;
        }

        /// <summary>Cancels any in-flight details load and drops the ViewModels.</summary>
        private void DisposeDetails()
        {
            _detailsViewModel?.Dispose();
            _detailsViewModel = null;
            _familyDetailsViewModel?.Dispose();
            _familyDetailsViewModel = null;
        }

        // ----- IModelItemDetailsHost: the details view drives the shell's workflows -----

        int IModelItemDetailsHost.ServerPort => LlamaManager.Shared.ServerPort;

        /// <summary>The play-glyph path, reused unchanged by the details Chat action.</summary>
        Task IModelItemDetailsHost.LoadModelAsync(ModelItem model)
        {
            if (model.IsLoading || model.IsLoaded || model.IsDownloading)
                return Task.CompletedTask;
            Log.Info("details: loading " + ((IModel)model).ServerModelId);
            model.LoadFailed = false;
            model.IsLoading = true;
            return LoadAndWatchAsync(model);
        }

        /// <summary>
        /// The catalog download path, reused: same disk + memory preflights,
        /// same move into the installed section. When the download starts the
        /// details view closes so the user watches the download ring in the
        /// list.
        /// </summary>
        async Task IModelItemDetailsHost.DownloadAsync(ModelItem model)
        {
            // Await the preflights (disk + memory): IsDownloading only
            // flips once both pass, and only then may the details close.
            await StartRecommendedDownloadAsync(model, DetailsView);
            if (model.IsDownloading)
                HideDetails();
        }

        /// <summary>
        /// Confirms with the same flyout UX as the row's trash glyph (a modal
        /// dialog could be stranded by the flyout's hide-on-deactivate), then
        /// deletes via the shared path and closes the details view.
        /// </summary>
        async Task<bool> IModelItemDetailsHost.DeleteAsync(ModelItem model)
        {
            if (model.IsLoaded || model.IsLoading || model.IsDownloading)
                return false;

            var confirmed = new TaskCompletionSource<bool>();
            var deleteButton = new Button
            {
                Content = "Delete",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 248, 81, 73)),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var flyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 10,
                    MaxWidth = 240,
                    Children =
                    {
                        new TextBlock { Text = "Delete this model?", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock
                        {
                            Text = "The downloaded files are removed from disk. You can download the model again at any time.",
                            FontSize = 12,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                            TextWrapping = TextWrapping.Wrap,
                        },
                        deleteButton,
                    },
                },
            };
            deleteButton.Click += (_, _) => { flyout.Hide(); confirmed.TrySetResult(true); };
            flyout.Closed += (_, _) => confirmed.TrySetResult(false); // light-dismiss = cancel
            flyout.ShowAt(DetailsView);

            if (!await confirmed.Task) return false;
            var deleted = await DeleteModelFromServerAsync(model);
            if (deleted) HideDetails();
            return deleted;
        }

        void IModelItemDetailsHost.CopyText(string text)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }

        async void IModelItemDetailsHost.OpenUri(string uri)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); }
            catch (Exception ex) { Log.Warn(ex, "open uri failed: " + uri); }
        }

        void IModelItemDetailsHost.CloseDetails() => HideDetails();

        // ----- IModelFamilyDetailsHost: the family details view drives the shell's workflows -----

        /// <summary>
        /// True when this exact build (repo + quant) is already installed —
        /// the variant row then reads "Installed" instead of offering a
        /// duplicate download. Same id forms the installed rows are keyed by
        /// (repo:quant; the server reports a bare repo mid-download).
        /// </summary>
        bool IModelFamilyDetailsHost.IsVariantInstalled(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build)
        {
            var serverId = build.Repo + ":" + build.Quant;
            return _localByServerId.ContainsKey(serverId) || FindLocalByRepo(serverId) is not null;
        }

        /// <summary>
        /// The catalog download path, reused for a family variant: materialize
        /// the build as the <see cref="ModelItem"/> the app already
        /// understands, run the same disk preflight + download workflow, then
        /// close the details view so the user watches the download ring in
        /// the list.
        /// </summary>
        void IModelFamilyDetailsHost.DownloadVariant(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build)
        {
            var item = new ModelItem
            {
                Name = $"{size.Name} ({build.Quant})",
                RepoName = build.Repo,
                Description = family.Description,
                Parameters = size.Params,
                Size = build.Size,
                SizeBytes = build.SizeBytes,
                License = family.License,
                Vision = size.Vision,
                Quant = build.Quant,
                Downloadable = true,
                Brand = family.Brand,
                Logo = ModelItem.ResolveLogo(family.Brand),
            };
            _ = StartVariantDownloadAsync(item);

            // Await the preflights (disk + memory): IsDownloading only flips
            // once both pass, and only then may the details close.
            async Task StartVariantDownloadAsync(ModelItem model)
            {
                await StartRecommendedDownloadAsync(model, FamilyDetailsView);
                if (model.IsDownloading)
                    HideDetails();
            }
        }

        void IModelFamilyDetailsHost.CloseDetails() => HideDetails();

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
        /// Stops a loaded model on the running llama server — the action behind
        /// the stop glyph next to the OpenInNewWindow glyph on a loaded row.
        /// Sends <c>POST /models/unload</c> and clears the row's loaded state
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
            foreach (var item in LocalModels)
                item.Logo = ModelItem.ResolveLogo(item.Brand);
            foreach (var family in Families)
                family.Logo = ModelItem.ResolveLogo(family.Brand);
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

            // The per-model context preference (chosen in the details view)
            // rides along as ctx_size; servers that predate the field ignore
            // the extra JSON member.
            var contextLength = Settings.Current.ModelContextLengths.TryGetValue(
                ((IModel)item).ServerModelId, out var t) ? t : (int?)null;

            bool ok;
            try
            {
                ok = await mgr.LoadModelAsync(item, progress, contextLength);
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
        /// Refreshes the footer's GPU indicator from the accelerator device
        /// probe (<see cref="LlamaManager.ListDevicesAsync"/> — cached for a
        /// minute there, so the StateChanged bursts don't spawn a process
        /// each). Shows the chip glyph when the llama binary sees a GPU
        /// (CUDA/Vulkan) and names the devices in its tooltip; stays hidden
        /// on CPU-only machines and before the binary is resolved (the
        /// probe then returns no devices and the state-change re-render
        /// picks them up once one appears). A failed probe never surfaces —
        /// the indicator simply stays as-is (fail-open, hint only).
        /// </summary>
        private async Task UpdateGpuIndicatorAsync()
        {
            IReadOnlyList<LlamaDevice> devices;
            try
            {
                devices = await LlamaManager.Shared.ListDevicesAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"GPU indicator probe failed: {ex.Message}");
                return;
            }

            var d = DeviceStatusPresentation.Describe(devices);

            // The probe awaits a child process; the continuation can land on
            // a thread-pool thread, and dependency-object writes must happen
            // on the UI thread.
            void Apply()
            {
                GpuIndicator.Visibility = d.Visible
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(GpuIndicator, d.ToolTip);
            }
            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess) Apply();
            else dq.TryEnqueue(Apply);
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
                _ = UpdateGpuIndicatorAsync();

                // A binary just appeared (resolved after startup or freshly
                // installed): the first fit evaluation may have run without
                // one and judged every family against CPU/RAM only — re-dim
                // with the real device probe.
                var binaryPath = LlamaManager.Shared.BinaryPath;
                if (binaryPath is not null && _lastBinaryPath is null &&
                    Families.Count > 0)
                    _ = EvaluateFamilyFitsAsync();
                _lastBinaryPath = binaryPath;

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
                    // Map the server's model states onto the row:
                    //   loaded     -> OpenInNewWindow glyph (IsLoaded, ring off)
                    //   sleeping   -> same as loaded (ServerModel.IsLoaded covers
                    //                 it): freed after the idle timeout but still
                    //                 the active model — it wakes on the next request
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
            // With a details view open, Esc steps back to the models list
            // instead of dismissing the whole flyout.
            if (DetailsView.Visibility == Visibility.Visible ||
                FamilyDetailsView.Visibility == Visibility.Visible)
                HideDetails();
            else
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
