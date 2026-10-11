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

        /// <summary>
        /// The rows of the bottom Hub-search section: the results of the most
        /// recent Hugging Face query (empty until the first search). Cleared
        /// and repopulated per search; row state ("added" checkmark) flows
        /// through bindings.
        /// </summary>
        public ObservableCollection<HubModelItemViewModel> HubResults { get; } = [];

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

        // Hub-search bookkeeping. _hubSearchId makes stale search responses
        // (an earlier query landing after a newer one) discardable; _hubSuggestId
        // does the same for the AutoSuggestBox's per-keystroke suggestions;
        // _hubDownloads links each Hub row to the ModelItem its download click
        // created, so a canceled first-download can re-enable the row's button.
        private int _hubSearchId;
        private int _hubSuggestId;

        // Per-attempt cancellation for the two hub-request paths. Each new
        // keystroke/search cancels the previous in-flight request's token so
        // superseded requests (up to 10 s) stop consuming sockets. The id
        // counters above remain the correctness mechanism for staleness;
        // cancellation is an additive optimization. Cancel-and-replace without
        // disposing (matching StopExternalDownloadWatch's convention) — the
        // token is already captured by the in-flight request. UI-thread-only.
        private CancellationTokenSource? _hubSuggestCts;
        private CancellationTokenSource? _hubSearchCts;

        // The query whose results HubResults currently holds — the load-more
        // page fetch uses it (NOT HubSearchBox.Text, which a picked suggestion
        // rewrites). Page fetches share _hubSearchId / _hubSearchCts with the
        // full search, so a newer search or page fetch supersedes the old one.
        private string? _hubPagedQuery;
        private readonly Dictionary<HubModelItemViewModel, ModelItem> _hubDownloads = new();

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
                UpdateHubRowStates();
            };

            LoadModels();
            LoadVersionInfo();
            UpdateServerStatusUI();
            _ = UpdateGpuIndicatorAsync();
            UpdateEmptyState();
            _ = LoadAvatarAsync();
            _ = CheckForAppUpdateAsync();

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

            // Preserve rows with an in-flight app-driven download. A populate can
            // race a just-started download (ReconcileAsync triggers a full reload
            // when the list was momentarily empty, e.g. right after a delete, and
            // it completes after the download's row was added). Clearing such a
            // row would orphan its driver (progress + cancel/pause) and the fresh
            // row would render the ring's pause button disabled — which is also
            // what painted a lighter disk behind the ring. Matched by repo (the
            // server may id a mid-download model by its bare repo, quant-less).
            var inFlight = _localByServerId.Values
                .Where(m => m.DownloadCancellation is not null)
                .Distinct()
                .ToList();
            var reused = new HashSet<ModelItem>();

            LocalModels.Clear();
            _localByServerId.Clear();
            foreach (var sm in serverModels)
            {
                var repo = SplitServerId(sm.Id).repo;
                var existing = inFlight.FirstOrDefault(m =>
                    !reused.Contains(m) &&
                    string.Equals(SplitServerId(((IModel)m).ServerModelId).repo, repo,
                        StringComparison.OrdinalIgnoreCase));
                ModelItem item;
                if (existing is not null)
                {
                    reused.Add(existing);
                    existing.IsLoaded = sm.IsLoaded;
                    existing.IsDownloading = sm.IsDownloading;
                    item = existing;
                }
                else
                {
                    item = BuildLocalItem(sm, byRepo);
                }
                _localByServerId[sm.Id] = item;
                LocalModels.Add(item);

                // Rows the brand-logo mapping can't fill (Hub-downloaded
                // models have no catalog brand) fall back to the author's
                // Hub avatar — disk first, then a fetch on miss (per-author
                // coalesced and globally bounded; see AttachCachedItemAvatarAsync).
                if (item.Logo is null)
                    _ = AttachCachedItemAvatarAsync(item);
            }

            // In-flight downloads the server hasn't listed yet (just POSTed) —
            // keep their rows too so the driver isn't orphaned.
            foreach (var m in inFlight.Where(m => !reused.Contains(m)))
            {
                _localByServerId[((IModel)m).ServerModelId] = m;
                LocalModels.Add(m);
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
            var external = sm.Id.StartsWith("local/", StringComparison.Ordinal);
            ulong localBytes = 0;
            if (external && sm.Path is not null)
            {
                try { localBytes = (ulong)new FileInfo(sm.Path!).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new ModelItem
            {
                Name = external && sm.Path is not null ? Path.GetFileNameWithoutExtension(sm.Path)
                    : DeriveDisplayName(repo, quant, byRepo),
                RepoName = repo,
                Quant = quant,
                IsExternalLocal = external,
                LocalFilePath = external ? sm.Path : null,
                Description = matched?.Description ?? "",
                Parameters = matched?.Parameters ?? "",
                Size = external && localBytes > 0 ? MemoryFit.FormatBytes(localBytes) : matched?.Size ?? "",
                SizeBytes = localBytes,
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

            // Move the model into the installed section (downloading). A row
            // that wasn't installed before (no file on disk) is flagged so a
            // later cancel/abandon removes it again instead of leaving a
            // zombie "available" entry; an already-installed row re-downloading
            // keeps its file either way.
            item.Downloadable = false;
            item.IsDownloading = true;
            if (!LocalModels.Contains(item))
                item.PendingFirstDownload = true;
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
            // source. DownloadModelAsync closes the SSE stream and asks the
            // server to abort the download when the token fires.
            using var cts = new CancellationTokenSource();
            item.DownloadCancellation = cts;

            // Progress toast: replaces itself in place under this tag, so a
            // long download streams its percent without flooding Action
            // Center. Shown only while the flyout is hidden (when it's visible
            // the row's ring tells the story) — the throttled progress
            // callback below toggles it either way as the flyout comes and
            // goes. Closed on every terminal path so no ghost toast
            // outlives the download.
            var toastTag = "download:" + ((IModel)item).ServerModelId;
            var toastShown = false;
            var cancelToastAction = new ToastAction("Cancel",
                ("action", "cancelDownload"), ("id", ((IModel)item).ServerModelId));
            void UpdateProgressToast(double fraction, string status)
            {
                if (IsFlyoutVisible)
                {
                    if (toastShown)
                    {
                        toastShown = false;
                        Notifications.Close(toastTag);
                    }
                }
                else
                {
                    toastShown = true;
                    Notifications.ShowProgress(toastTag,
                        "Downloading " + item.DisplayName, status, fraction, cancelToastAction);
                }
            }
            // Reset any stale detail from a previous (failed or paused) attempt
            // — the subtitle shows the live detail line as soon as a size is
            // known. Fresh SSE progress events repopulate the byte counts.
            item.DownloadPaused = false;
            // Clear any stale failure (and its classified detail) from a
            // previous failed attempt — a new download starts clean.
            item.DownloadFailed = false;
            item.DownloadedBytes = 0;
            item.DownloadTotalBytes = 0;
            item.DownloadBytesPerSecond = 0;

            // Throttle UI updates: the server streams an SSE progress event per
            // chunk (potentially hundreds/sec), and each one would otherwise
            // enqueue a UI-thread callback. The ring + percent caption only need
            // ~10 updates/sec. Terminal events (Done/Failed) always pass through
            // so the final state lands immediately.
            long lastProgressApplyMs = 0;
            // The toast updates even less often: ~1/sec is plenty for a
            // progress bar, and every update is a notification-platform call.
            long lastToastUpdateMs = 0;
            long lastSampleBytes = 0, lastSampleMs = 0;
            double bytesPerSecond = 0;
            string? serverMessage = null;
            int? serverHttpStatus = null;
            string? serverExceptionType = null;
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                var now = Environment.TickCount64;
                if (!p.Done && !p.Failed && now - lastProgressApplyMs < 100) return;
                lastProgressApplyMs = now;

                // Progress toast, on its own ~1/sec throttle (a toast update is
                // a notification-platform call — far pricier than a UI-thread
                // Apply). Runs on the UI thread like the rest of this callback.
                if (!p.Done && !p.Failed && now - lastToastUpdateMs >= 1000)
                {
                    lastToastUpdateMs = now;
                    if (p.TotalBytes > 0)
                        UpdateProgressToast(p.Fraction,
                            DownloadToastStatus(p.Fraction, bytesPerSecond));
                }

                // The server's rejection detail (POST error body, stream
                // failure) plus the optional classification inputs (HTTP status
                // / exception type) — surfaced in the failure toast and
                // classified for the row + toast.
                if (p.Failed)
                {
                    if (!string.IsNullOrWhiteSpace(p.Message))
                        serverMessage = p.Message;
                    serverHttpStatus = p.HttpStatus;
                    serverExceptionType = p.ExceptionType;
                }

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
                    if (toastShown) { toastShown = false; Notifications.Close(toastTag); }
                    item.IsDownloading = false;
                    // A pause click that raced the completion is discarded —
                    // the download is over, there is nothing left to resume.
                    item.DownloadPaused = false;
                    if (ok)
                    {
                        // Download done — the file exists now, so the row is
                        // a real installed model; a later cancel of some
                        // re-download must not remove it.
                        item.PendingFirstDownload = false;
                        // Load it. The row now shows the load ring until the
                        // poller reports the model as loaded.
                        item.LoadFailed = false;
                        item.IsLoading = true;
                        _ = LoadAndWatchAsync(item);
                    }
                    else
                    {
                        // Classify the failure (HTTP status / SSE error text /
                        // exception type) into actionable guidance while
                        // preserving the raw detail on the row + log. The toast
                        // still carries a Retry button that routes straight
                        // back into the row's retry path (App handles the
                        // activation) — the user never has to reopen the flyout
                        // to start the download over.
                        var failure = DownloadFailurePresentation.Classify(
                            serverHttpStatus, serverMessage, serverExceptionType);
                        item.DownloadFailureInfo = failure;
                        item.DownloadFailed = true;
                        NotifyWhenHidden("Download failed",
                            DownloadFailurePresentation.ToastBody(item.DisplayName, failure),
                            new ToastAction("Retry",
                                ("action", "retryDownload"),
                                ("id", ((IModel)item).ServerModelId)));
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
                // clicking the ring — the server has already been asked to
                // abort (see DownloadModelAsync). A cancel of a first-time
                // download removes the row from the installed list (no file
                // ever landed — leaving it would show a "playable" model
                // whose load can only fail); a pause (DownloadPaused set by
                // the click) lands the row on the resume glyph instead, and a
                // canceled re-download of an installed model keeps its row.
                // Either way the server drops the partial bytes on abort —
                // the next attempt starts the download over.
                void Abort()
                {
                    if (toastShown) { toastShown = false; Notifications.Close(toastTag); }
                    if (item is { PendingFirstDownload: true, DownloadPaused: false })
                        RemovePendingDownloadRow(item);
                    else
                        item.IsDownloading = false;
                }
                if (queue is null || queue.HasThreadAccess)
                    Abort();
                else
                    queue.TryEnqueue(Abort);
            }
            catch (Exception ex)
            {
                // A throw from the stream open / mid-loop (transport fault,
                // unparseable event) rather than a clean Failed report. Log it
                // (this path had no message or log before) and classify from
                // the captured server evidence when present, else from the
                // exception itself so the row + toast still explain something.
                Log.Error(ex, $"download for {((IModel)item).ServerModelId} failed unexpectedly");
                var failureHttpStatus = serverMessage is not null ? serverHttpStatus : null;
                var failureDetail = serverMessage ?? ex.Message;
                var failureExceptionType = serverMessage is not null ? serverExceptionType : ex.GetType().Name;
                void Fail()
                {
                    if (toastShown) { toastShown = false; Notifications.Close(toastTag); }
                    item.IsDownloading = false;
                    item.DownloadPaused = false;
                    var failure = DownloadFailurePresentation.Classify(
                        failureHttpStatus, failureDetail, failureExceptionType);
                    item.DownloadFailureInfo = failure;
                    item.DownloadFailed = true;
                    NotifyWhenHidden("Download failed",
                        DownloadFailurePresentation.ToastBody(item.DisplayName, failure),
                        new ToastAction("Retry",
                            ("action", "retryDownload"),
                            ("id", ((IModel)item).ServerModelId)));
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
        /// The progress toast's status line — percent + smoothed speed, e.g.
        /// "42% · 12.3 MB/s". A stalled stream shows just the percent, never a
        /// bogus "0 B/s".
        /// </summary>
        private static string DownloadToastStatus(double fraction, double bytesPerSecond)
        {
            var pct = (int)Math.Round(fraction * 100);
            return bytesPerSecond > 0
                ? $"{pct}% · {DownloadProgressPresentation.FormatBytes(bytesPerSecond)}/s"
                : $"{pct}%";
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
        /// to the play glyph. The server drops the partial bytes when a
        /// download is aborted, so the next attempt re-downloads from scratch.
        /// While paused the button abandons the partial instead — the server
        /// side is already stopped, so there's nothing to cancel.
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
                // pause was requested — just abandon the partial. A first-
                // time download's row disappears from the installed list
                // (nothing on disk to play); an installed model's
                // re-download returns the row to the play glyph.
                Log.Info("cancel clicked: abandoning paused download of " + ((IModel)item).ServerModelId);
                if (item.PendingFirstDownload)
                {
                    RemovePendingDownloadRow(item);
                    return;
                }
                item.DownloadPaused = false;
                item.DownloadFraction = 0;
                item.DownloadedBytes = 0;
                item.DownloadTotalBytes = 0;
                item.DownloadBytesPerSecond = 0;
                return;
            }

            Log.Info("cancel clicked: cancelling download of " + ((IModel)item).ServerModelId);

            // Ask the server to abort the transfer first — the cancellation
            // below only closes this app's SSE watcher, and the poller would
            // otherwise keep the row alive (resurrected from the server
            // snapshot, ring and all) until the server finished downloading.
            _ = LlamaManager.Shared.CancelServerDownloadAsync(((IModel)item).ServerModelId);

            try { item.DownloadCancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* download finished between check and click */ }

            // Tear the row down immediately rather than waiting for the
            // driver's cancellation unwind (its SSE read can sit on a canceled
            // token until the stream next yields, leaving the ring up and the
            // row in the installed list). Same cleanup the driver's Abort()
            // path performs — idempotent with it.
            if (item.PendingFirstDownload)
            {
                RemovePendingDownloadRow(item);
            }
            else
            {
                // An installed model's re-download keeps its row (the file is
                // on disk) — reset it to the play glyph.
                item.IsDownloading = false;
                item.DownloadFraction = 0;
                item.DownloadedBytes = 0;
                item.DownloadTotalBytes = 0;
                item.DownloadBytesPerSecond = 0;
            }
        }

        /// <summary>
        /// Drops a row whose first download never completed: it was added to
        /// the installed list only to host the download, no file ever landed
        /// on disk, and leaving it would show a "playable" model whose load
        /// attempt can only fail (the error-glyph zombie the cancel button
        /// used to leave behind). The item is reset to a clean catalog state
        /// so the family view offers it for download again.
        /// </summary>
        private void RemovePendingDownloadRow(ModelItem item)
        {
            StopExternalDownloadWatch(item);
            foreach (var key in _localByServerId
                         .Where(kv => ReferenceEquals(kv.Value, item))
                         .Select(kv => kv.Key).ToList())
                _localByServerId.Remove(key);
            LocalModels.Remove(item);

            item.IsDownloading = false;
            item.DownloadPaused = false;
            item.DownloadFailed = false;
            item.LoadFailed = false;
            item.DownloadFraction = 0;
            item.DownloadedBytes = 0;
            item.DownloadTotalBytes = 0;
            item.DownloadBytesPerSecond = 0;
            item.Downloadable = true;
            item.PendingFirstDownload = false;

            UpdateEmptyState();
        }

        /// <summary>
        /// Fired when the download progress ring is tapped: pauses the
        /// download. Pause reuses the cancel path — the cancellation unwinds
        /// <see cref="DownloadAndLaunchAsync"/> and asks the server to abort —
        /// but with <see cref="ModelItem.DownloadPaused"/> set first, so the
        /// row lands on the resume glyph instead of the play glyph. The
        /// server drops the partial bytes when the download is aborted, so
        /// resuming re-downloads from scratch. No-op for externally-triggered
        /// downloads (the ring's button is
        /// disabled then — see <see cref="ModelItem.CanPauseDownload"/>).
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
        /// download → load lifecycle. The pause's abort dropped the partial
        /// bytes server-side, so this starts the download over.
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

        // ---- Toast action routing ----

        /// <summary>
        /// Resolves a model row from a toast action's id and mirrors the row
        /// retry glyph's behavior (see <see cref="LocalModelRetryDownload_Click"/>).
        /// Called by App when the user taps Retry on a download-failed toast;
        /// a stale toast (row gone, or already retrying/downloading) is a
        /// no-op — the toast may outlive the state it was shown for.
        /// </summary>
        public void RetryDownloadFromToast(string? serverModelId)
        {
            var item = FindRowByServerId(serverModelId);
            if (item is null) return;
            if (item.IsDownloading || !item.DownloadFailed) return;

            Log.Info("retry clicked (toast): re-downloading " + serverModelId);
            item.DownloadFailed = false;
            item.IsDownloading = true;
            _ = DownloadAndLaunchAsync(item);
        }

        /// <summary>
        /// Cancels a running download from its progress toast's Cancel button.
        /// Same cancellation source as the row's own cancel button, so the
        /// abort path (SSE close + server-side abort) is identical.
        /// </summary>
        public void CancelDownloadFromToast(string? serverModelId)
        {
            var item = FindRowByServerId(serverModelId);
            if (item?.DownloadCancellation is { } cts) cts.Cancel();
        }

        private ModelItem? FindRowByServerId(string? serverModelId)
        {
            if (serverModelId is null)
            {
                Log.Warn("toast action carried no model id");
                return null;
            }
            var item = LocalModels.FirstOrDefault(m => ((IModel)m).ServerModelId == serverModelId);
            if (item is null)
                Log.Warn("toast action: no row for " + serverModelId);
            return item;
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
        /// <c>DELETE /models?model={name}</c>; on success the row is removed from
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
            var flyout = new Flyout
            {
                Content = new TextBlock
                {
                    Text = item.IsExternalLocal
                        ? "Couldn't complete model deletion. The file may be in use or the folder may not be writable. If the file was removed but the model is still listed, restart Llama to refresh its presets."
                        : "Couldn't delete this model. Make sure the server is running and the model is unloaded, then try again.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280,
                },
            };
            flyout.ShowAt(_detailsViewModel is null ? ModelsList : DetailsView);
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
                    // The router caches each model's preset at load; re-render
                    // the --models-preset INI and ask it to re-read so the next
                    // load spawns the child with the new --ctx-size.
                    _ = LlamaManager.Shared.ReloadModelPresetsAsync();
                },
                loadPromptProfile: id =>
                    Settings.Current.ModelPromptProfiles.TryGetValue(id, out var profile) ? profile : null,
                savePromptProfile: (id, profile) =>
                {
                    if (profile is null) Settings.Current.ModelPromptProfiles.Remove(id);
                    else Settings.Current.ModelPromptProfiles[id] = profile;
                    Settings.Current.Save();
                    _ = LlamaManager.Shared.ReloadModelPresetsAsync();
                },
                globalBatchSize: Settings.Current.BatchSize,
                globalMicroBatchSize: Settings.Current.MicroBatchSize,
                experienceMode: Settings.Current.ExperienceMode,
                globalCacheTypeK: Settings.Current.CacheTypeK,
                globalCacheTypeV: Settings.Current.CacheTypeV,
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

        string IModelItemDetailsHost.ServerAddress => LlamaManager.Shared.ConnectAddress;

        string? IModelItemDetailsHost.ApiKey => LlamaManager.Shared.ApiKey;

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
                            Text = model.DeleteConfirmationText,
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
        /// (repo:quant; the server reports a bare repo mid-download). The
        /// repo-level fallback only counts a row whose quant matches the
        /// variant's — otherwise downloading one quant (say Q4_0) would mark
        /// every sibling quant of the family (Q8_0, …) as installed too.
        /// </summary>
        bool IModelFamilyDetailsHost.IsVariantInstalled(ModelFamily family, ModelFamilySize size, ModelFamilyBuild build)
        {
            var serverId = build.Repo + ":" + build.Quant;
            if (_localByServerId.ContainsKey(serverId)) return true;
            var row = FindLocalByRepo(serverId);
            return row is not null &&
                   string.Equals(row.Quant, build.Quant, StringComparison.OrdinalIgnoreCase);
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

            // No api_key in the URL: it would leak into browser history and
            // the llama.cpp WebUI reads its key from a dialog anyway. The key
            // lives in Settings (copyable) if the WebUI asks for it.
            await Windows.System.Launcher.LaunchUriAsync(new Uri(
                ApiRequestPresentation.BuildWebUiUrl(
                    LlamaManager.Shared.ConnectAddress, LlamaManager.Shared.ServerPort,
                    serverModelId)));
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

        /// <summary>
        /// <see cref="NotifyWhenHidden(string, string)"/> with action buttons
        /// (e.g. Retry on a download-failure toast).
        /// </summary>
        private void NotifyWhenHidden(string title, string body, params ToastAction[] actions)
        {
            if (!IsFlyoutVisible)
                Notifications.Show(title, body, actions);
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
            DevBuildBadge.Visibility = IsPackaged()
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
        }

        /// <summary>
        /// True when running from the MSIX package (every official release);
        /// false for a loose-files dev build, which gets the footer's "dev" label.
        /// </summary>
        private static bool IsPackaged()
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
        }

        /// <summary>
        /// Asks GitHub whether a release newer than the running version
        /// exists (see <see cref="UpdateChecker"/>) and, when one does,
        /// lights up the header's soft-yellow update banner with a link to
        /// the release page. Failures are logged (not raised) — the banner
        /// stays hidden.
        ///
        /// NOTE: the awaited HTTP fetch completes on a threadpool thread,
        /// but WinUI requires all DependencyObject writes on the UI thread —
        /// so the post-await XAML work is marshalled via the
        /// <see cref="DispatcherQueue"/> captured before the await (the same
        /// pattern every other async helper in this class uses). Skipping
        /// that marshal throws a COMException that the catch below swallows,
        /// leaving the banner silently invisible — the exact failure we
        /// spent a step debugging.
        /// </summary>
        private async Task CheckForAppUpdateAsync()
        {
            var queue = DispatcherQueue; // captured on the UI thread, before the first await
            try
            {
                var current = Assembly.GetExecutingAssembly().GetName().Version;
                if (current is null)
                {
                    Log.Warn("cannot determine the running app version; skipping the update check");
                    return;
                }

                var update = await UpdateChecker.GetLatestUpdateAsync(current);

                // Applying does nothing when there's no update, so enqueueing
                // unconditionally on completion keeps the two code paths (UI
                // thread vs dispatched) identical.
                void Apply()
                {
                    if (update is null) return; // up to date, or the check failed (logged)
                    UpdateLink.Content = update.Tag;
                    UpdateLink.NavigateUri = update.ReleasePageUrl;
                    UpdateBanner.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }

                if (queue is null || queue.HasThreadAccess) Apply();
                else queue.TryEnqueue(Apply);
            }
            catch (Exception ex)
            {
                // Best-effort: a failed check simply leaves the banner hidden.
                Log.Warn(ex, "app update banner setup failed; staying hidden");
            }
        }

        private string? _lastModelProfileWarning;

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
            var warning = LlamaManager.Shared.ModelProfileWarning;
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ServerStatusDot,
                warning is null ? d.ToolTip : d.ToolTip + "\n\n" + warning);
            if (warning != _lastModelProfileWarning)
            {
                _lastModelProfileWarning = warning;
                if (warning is not null) Notifications.Show("Model overrides not applied", warning);
            }
            ServerRestartButton.Visibility = d.CanRelaunch
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        /// <summary>
        /// Refreshes the footer's device indicator from the accelerator
        /// device probe (<see cref="LlamaManager.ProbeDevicesAsync"/> —
        /// cached for a minute there, so the StateChanged bursts don't spawn
        /// a process each). Shows the GPU card glyph (accent color) when the
        /// llama binary sees a GPU (CUDA/Vulkan) and names the devices in
        /// its tooltip; shows the dimmed CPU chip glyph when the probe
        /// succeeded but found no accelerator. A failed probe or an
        /// unresolved binary never surfaces — the indicator simply stays
        /// hidden (fail-open, hint only); the state-change re-render picks
        /// the devices up once a probe succeeds.
        /// </summary>
        private async Task UpdateGpuIndicatorAsync()
        {
            DeviceProbe probe;
            IReadOnlyList<LlamaDevice> selectedDevices;
            try
            {
                probe = await LlamaManager.Shared.ProbeDevicesAsync();
                selectedDevices = await LlamaManager.Shared.ListDevicesAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"GPU indicator probe failed: {ex.Message}");
                return;
            }

            var activeDeviceId = LlamaManager.Shared.ServerStatus == LlamaManager.ServerState.Running
                ? LlamaManager.Shared.ActiveDeviceId : null;
            var d = DeviceStatusPresentation.Describe(probe.Succeeded, selectedDevices,
                activeDeviceId, LlamaManager.Shared.RuntimeFallbackReason);

            // The probe awaits a child process; the continuation can land on
            // a thread-pool thread, and dependency-object writes must happen
            // on the UI thread.
            void Apply()
            {
                GpuIndicator.Visibility = d.Kind == DeviceStatusPresentation.IndicatorKind.Gpu
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
                CpuIndicator.Visibility = d.Kind == DeviceStatusPresentation.IndicatorKind.Cpu
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(GpuIndicator, d.ToolTip);
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(CpuIndicator, d.ToolTip);
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
                    // Server truth: any state but "downloading" means the model
                    // exists in the cache — no longer a pending first download.
                    if (!sm.IsDownloading)
                        item.PendingFirstDownload = false;

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
                            // The Chat button jumps straight to the overlay —
                            // the reason the model was loaded in the first
                            // place — instead of just opening the flyout.
                            NotifyWhenHidden("Model ready",
                                $"{item.DisplayName} is loaded and ready to chat.",
                                new ToastAction("Chat", ("action", "chat")));
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
                    // An externally-triggered download has no file yet either —
                    // the same vanish rules apply if it gets canceled out from
                    // under us (the sweep below drops the zombie row).
                    if (sm.IsDownloading)
                        newItem.PendingFirstDownload = true;
                    _localByServerId[sm.Id] = newItem;
                    LocalModels.Add(newItem);
                    // Disk first, Hub fetch on miss (per-author coalesced and
                    // globally bounded; see AttachCachedItemAvatarAsync).
                    if (newItem.Logo is null)
                        _ = AttachCachedItemAvatarAsync(newItem);
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
            foreach (var (key, row) in _localByServerId.ToList())
            {
                if (row.IsDownloading && row.DownloadCancellation is null &&
                    !serverIds.Contains(key))
                {
                    Log.Info("download vanished from /models: " + key);
                    if (row.PendingFirstDownload)
                    {
                        // No file ever landed — without removal the row would
                        // sit in the installed list as a "playable" model
                        // whose load attempt can only fail.
                        RemovePendingDownloadRow(row);
                    }
                    else
                    {
                        StopExternalDownloadWatch(row);
                        row.IsDownloading = false;
                        row.DownloadFraction = 0;
                    }
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

            // Pass the full server id (repo or repo:quant, however the server
            // keys the download) — the watcher matches SSE events on the repo
            // part, so both key forms resolve to the same download.
            var cts = new CancellationTokenSource();
            _externalDownloadWatches[item] = cts;
            Log.Info("watching external download: " + serverId);
            _ = WatchExternalDownloadAsync(item, serverId, cts);
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

        private async Task WatchExternalDownloadAsync(ModelItem item, string serverId, CancellationTokenSource cts)
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
                await LlamaManager.Shared.WatchDownloadAsync(serverId, progress, cts.Token);
            }
            catch (Exception ex)
            {
                // Fire-and-forget: nothing upstream would observe a fault.
                Log.Warn(ex, "external download watch faulted: " + serverId);
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
        /// before <c>:</c>). Older servers id a mid-download model by its bare
        /// repo (the quant resolves only once the download completes), so an
        /// exact <see cref="_localByServerId"/> lookup can miss rows that were
        /// keyed <c>repo:quant</c> (e.g. moved from Recommended on tap).
        ///
        /// <para>When several rows share the repo (multiple quants installed),
        /// an exact quant match wins; a bare-repo id (mid-download) prefers a
        /// row that is itself mid-download, so the transient id never steals
        /// another quant's key and duplicates the model in the list.</para>
        /// </summary>
        private ModelItem? FindLocalByRepo(string serverId)
        {
            var (repo, quant) = SplitServerId(serverId);
            ModelItem? fallback = null;
            foreach (var (key, row) in _localByServerId)
            {
                var (rowRepo, rowQuant) = SplitServerId(key);
                if (!string.Equals(rowRepo, repo, StringComparison.OrdinalIgnoreCase)) continue;

                // Both ids carry a quant and they agree — exact hit.
                if (quant.Length > 0 && rowQuant.Length > 0 &&
                    string.Equals(rowQuant, quant, StringComparison.OrdinalIgnoreCase))
                    return row;

                // Bare-repo ids belong to the download in flight.
                if (quant.Length == 0 && row.IsDownloading) return row;

                fallback ??= row;
            }
            return fallback;
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
            // No api_key in the URL (browser history leak; the WebUI reads its
            // key from a typed dialog — copyable from Settings).
            await Windows.System.Launcher.LaunchUriAsync(
                new System.Uri($"http://{LlamaManager.Shared.ConnectAddress}:{LlamaManager.Shared.ServerPort}"));
        }

        /// Opens llama.app in the default browser when the brand logo is
        /// clicked. Hide the flyout first so the browser doesn't come up
        /// behind it.
        private async void Logo_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            HideFlyout();
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://llama.app")); }
            catch { /* Ignore launch failures (e.g. no default browser set) */ }
        }

        private void Settings_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => OpenSettings();

        /// <summary>
        /// Opens the Settings window (shared by the flyout's gear and the tray
        /// context menu). Hides the flyout first so the settings dialog isn't
        /// drawn behind it (the flyout would otherwise immediately deactivate
        /// and hide on its own, but doing it explicitly avoids a flash).
        /// </summary>
        public void OpenSettings()
        {
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

        // ---- Hub search (bottom section) ----

        /// <summary>
        /// Drives the Hub search box's suggestions while the user types:
        /// debounced (one Hub request per settled keystroke burst, stale
        /// responses discarded by id) top-GGUF-repo matches, ranked by
        /// downloads. Enter, a picked suggestion, or the magnifier runs the
        /// full search via <see cref="HubSearchBox_QuerySubmitted"/>.
        /// </summary>
        private async void HubSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = sender.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                sender.ItemsSource = null;
                return;
            }

            var suggestId = ++_hubSuggestId;
            _hubSuggestCts?.Cancel();
            _hubSuggestCts = new CancellationTokenSource();
            var cancel = _hubSuggestCts.Token;
            try { await Task.Delay(300, cancel); }
            catch (OperationCanceledException) { return; }
            if (suggestId != _hubSuggestId) return; // a newer keystroke superseded us

            try
            {
                var token = Settings.Current.HuggingFaceToken;
                var results = await new HubClient(string.IsNullOrWhiteSpace(token) ? null : token)
                    .SearchModels(query, cancel, limit: 6);
                if (suggestId != _hubSuggestId) return;
                // Suggestions are ranked by likes (most-liked first) — the
                // dropdown's pick list, unlike the full search beneath (which
                // keeps the Hub's download ranking). Ties fall back to the
                // Hub's download order (OrderByDescending is stable).
                //
                // The list is then REVERSED: the search box sits at the
                // window's bottom edge, so the suggestion popup opens upward
                // and anchors the FIRST ItemsSource item nearest the query
                // box — without the reversal the most-liked suggestion would
                // sit at the bottom of the dropdown (observed: the exact
                // reverse of the sorted list rendered, twice, across
                // restarts). Reversing keeps most-liked at the visual top.
                sender.ItemsSource = ToHubRows(results)
                    .OrderByDescending(r => r.Likes)
                    .Take(6)
                    .Reverse()
                    .ToList();
            }
            catch (Exception ex)
            {
                // Offline / rejected — suggestions are a best-effort affordance;
                // the full search's status line carries the error if one runs.
                Log.Debug("hub suggestions failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Runs the full Hub search: Enter in the box, the magnifier, or a
        /// picked suggestion (whose repo id fills the box via
        /// TextMemberPath). The chosen suggestion is the authoritative query —
        /// its repo id replaces whatever partial text is in the box.
        /// </summary>
        private void HubSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = args.ChosenSuggestion is HubModelItemViewModel vm
                ? vm.RepoId
                : args.QueryText?.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            if (!string.Equals(HubSearchBox.Text, query, StringComparison.Ordinal))
                HubSearchBox.Text = query; // programmatic — TextChanged ignores it
            _ = RunHubSearchAsync();
        }

        /// <summary>
        /// Queries the Hugging Face Hub for GGUF models matching the search
        /// box's text and fills <see cref="HubResults"/>. The status line
        /// above the results mirrors the browse section's (ring while in
        /// flight, a count / no-results / error caption). Stale responses —
        /// an earlier query landing after a newer one was started — are
        /// discarded by id, so rapid Enter presses never interleave results.
        /// </summary>
        private async Task RunHubSearchAsync()
        {
            var query = HubSearchBox.Text?.Trim() ?? "";
            if (query.Length == 0) return;

            var searchId = ++_hubSearchId;
            _hubSearchCts?.Cancel();
            _hubSearchCts = new CancellationTokenSource();
            var cancel = _hubSearchCts.Token;
            HubResultsPanel.Visibility = Visibility.Visible;
            HubResultsList.Visibility = Visibility.Collapsed;
            HubStatusRing.Visibility = Visibility.Visible;
            HubStatusText.Text = $"Searching Hugging Face for \u201C{query}\u201D\u2026";

            // The token is optional for search (it only lifts rate limits and
            // unlocks gated repos) — read it before the try so the catch
            // clauses can gate token-specific guidance on it.
            var token = Settings.Current.HuggingFaceToken;
            var tokenConfigured = !string.IsNullOrWhiteSpace(token);

            try
            {
                var results = await new HubClient(string.IsNullOrWhiteSpace(token) ? null : token)
                    .SearchModels(query, cancel);

                if (searchId != _hubSearchId) return; // a newer search superseded us

                HubResults.Clear();
                foreach (var row in ToHubRows(results))
                {
                    HubResults.Add(row);
                    // Disk cache only — a full page of results must never
                    // fire a request storm; the fetch happens at download
                    // time (AttachItemAvatarAsync) and every later search
                    // shows the author immediately.
                    _ = AttachHubAvatarAsync(row);
                }
                UpdateHubRowStates();

                _hubPagedQuery = query;
                // A full page means there may be more — append the single
                // load-more sentinel. The status line keeps the page-1 count
                // after appends (mid-list appends don't rewrite it; the count
                // wording is frozen for a separate status-message task).
                if (HubSearchPagination.PossiblyHasNextPage(results.Count, HubClient.DefaultSearchLimit))
                    HubResults.Add(NewHubLoadMoreRow());

                HubStatusRing.Visibility = Visibility.Collapsed;
                HubResultsList.Visibility = results.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;
                HubStatusText.Text = results.Count == 0
                    ? HubSearchFailurePresentation.NoResultsCaption(query)
                    : $"{results.Count} GGUF repos for \u201C{query}\u201D";
            }
            catch (HubSearchException ex)
            {
                // The Hub answered with a non-success status (429 rate limit,
                // 401/403 auth, …) — classify it into actionable guidance.
                if (searchId != _hubSearchId) return;
                var failure = HubSearchFailurePresentation.Classify(ex, tokenConfigured);
                // Rate-limit/auth failures carry actionable guidance and are
                // logged at debug; a typed status that still lands in Unknown
                // (e.g. 5xx, anonymous 401/403) must leave a Warn-level trace
                // like the generic catch below.
                if (failure.Kind == HubSearchFailureKind.Unknown)
                    Log.Warn(ex, "hub search failed");
                else
                    Log.Debug($"hub search rejected: HTTP {ex.Status}");
                HubStatusRing.Visibility = Visibility.Collapsed;
                HubResultsList.Visibility = Visibility.Collapsed;
                HubStatusText.Text = HubSearchFailurePresentation.StatusText(failure);
            }
            catch (Exception ex) when (ex is HttpRequestException
                or TaskCanceledException or TimeoutException)
            {
                // Network failure / timeout — say so instead of "no results".
                if (searchId != _hubSearchId) return;
                HubStatusRing.Visibility = Visibility.Collapsed;
                HubResultsList.Visibility = Visibility.Collapsed;
                HubStatusText.Text =
                    "Couldn't reach Hugging Face. Check your connection and try again.";
            }
            catch (Exception ex)
            {
                if (searchId != _hubSearchId) return;
                Log.Warn(ex, "hub search failed");
                HubStatusRing.Visibility = Visibility.Collapsed;
                HubResultsList.Visibility = Visibility.Collapsed;
                HubStatusText.Text = "Search failed. Try again.";
            }
        }

        /// <summary>
        /// Fired by the load-more row: fetches the next page of the current
        /// query (skip = number of real result rows already shown) and appends
        /// it in place. Shares the full search's id/CTS bookkeeping, so a newer
        /// search or page fetch supersedes this one; a failure flips the row to
        /// a recoverable "Retry" and never touches the shown results.
        /// </summary>
        private async void HubLoadMore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not HubModelItemViewModel vm)
                return;
            if (!vm.IsLoadMoreRow || vm.LoadMoreBusy) return; // guard double-fetch
            var query = _hubPagedQuery;
            if (string.IsNullOrWhiteSpace(query)) return;

            var searchId = ++_hubSearchId;
            _hubSearchCts?.Cancel();
            _hubSearchCts = new CancellationTokenSource();
            var cancel = _hubSearchCts.Token;

            // Busy flips synchronously before the first await; skip counts the
            // REAL result rows (the sentinel excluded), captured pre-await so a
            // concurrent change can't shift the offset.
            vm.LoadMoreBusy = true;
            vm.LoadMoreFailed = false;
            var skip = HubSearchPagination.NextSkip(CountHubResultRows(HubResults));

            try
            {
                // The token is optional for search — pass it when configured.
                var token = Settings.Current.HuggingFaceToken;
                var results = await new HubClient(string.IsNullOrWhiteSpace(token) ? null : token)
                    .SearchModels(query, cancel, skip: skip);

                if (searchId != _hubSearchId) return; // superseded by a newer search/page fetch

                // Append in place — never Clear: the already-shown rows (and
                // their avatars/download state) must survive a page fetch.
                HubResults.Remove(vm);
                foreach (var row in ToHubRows(results))
                {
                    HubResults.Add(row);
                    _ = AttachHubAvatarAsync(row);
                }
                UpdateHubRowStates();

                // A full page means there may be more; a short/empty page ends
                // the list — the affordance is not reattached.
                if (HubSearchPagination.PossiblyHasNextPage(results.Count, HubClient.DefaultSearchLimit))
                    HubResults.Add(NewHubLoadMoreRow());
            }
            catch (Exception ex) when (ex is HttpRequestException
                or TaskCanceledException or TimeoutException)
            {
                // Superseded (id changed / token canceled) is not a failure —
                // only a real network error flips the row to Retry. The shown
                // results are never touched.
                if (searchId != _hubSearchId) return;
                if (cancel.IsCancellationRequested) return;
                vm.LoadMoreBusy = false;
                vm.LoadMoreFailed = true;
            }
            catch (Exception ex)
            {
                if (searchId != _hubSearchId) return;
                if (cancel.IsCancellationRequested) return;
                Log.Warn(ex, "hub load more failed");
                vm.LoadMoreBusy = false;
                vm.LoadMoreFailed = true;
            }
        }

        /// <summary>Creates the synthetic "Show more" row appended after a full page.</summary>
        private static HubModelItemViewModel NewHubLoadMoreRow() => new() { IsLoadMoreRow = true };

        /// <summary>
        /// The number of REAL result rows in the list — the load-more sentinel
        /// is excluded, so this is both the offset the next page is fetched at
        /// and the "shown so far" count. Shared with the pagination tests.
        /// </summary>
        internal static int CountHubResultRows(IReadOnlyList<HubModelItemViewModel> rows)
        {
            var count = 0;
            foreach (var row in rows)
            {
                if (!row.IsLoadMoreRow) count++;
            }
            return count;
        }

        /// <summary>
        /// Maps Hub search results onto the row view-models the results list
        /// and the AutoSuggestBox dropdown both render: the repo id split
        /// into its display name (last path segment) and author (first).
        /// </summary>
        private static List<HubModelItemViewModel> ToHubRows(List<HubClient.HubSearchResult> results)
        {
            var rows = new List<HubModelItemViewModel>(results.Count);
            foreach (var r in results)
            {
                var sep = r.Id.LastIndexOf('/');
                rows.Add(new HubModelItemViewModel
                {
                    RepoId = r.Id,
                    DisplayName = sep >= 0 ? r.Id[(sep + 1)..] : r.Id,
                    Author = sep > 0 ? r.Id[..sep] : "",
                    Downloads = r.Downloads,
                    Likes = r.Likes,
                    LastModified = r.LastModified,
                });
            }
            return rows;
        }

        /// <summary>
        /// Attaches the author's Hub avatar to a search-result row once it
        /// lands — disk cache only, so populating a full page of results
        /// never fires a request storm. Authors seen before (their model was
        /// downloaded) show their avatar immediately; the fetch happens at
        /// download time (<see cref="AttachItemAvatarAsync"/>).
        /// </summary>
        private async Task AttachHubAvatarAsync(HubModelItemViewModel row)
        {
            var avatar = await AvatarCache.GetAsync(row.Author);
            if (avatar is not null && HubResults.Contains(row))
                row.Logo = avatar;
        }

        /// <summary>
        /// Attaches the author's Hub avatar to an installed-list row once it
        /// lands — fetching it from the Hub on first use and storing it under
        /// the app's local cache for reuse (<see cref="AvatarCache"/>).
        /// Avatars are decorative: a failure just leaves the empty tile.
        /// </summary>
        private async Task AttachItemAvatarAsync(ModelItem item)
        {
            var avatar = await AvatarCache.GetOrFetchAsync(AuthorOf(item.RepoName ?? item.Name));
            if (avatar is not null) item.Logo = avatar;
        }

        /// <summary>
        /// Attaches a Hub avatar to a row the brand-logo mapping can't fill —
        /// disk cache first, fetching from the Hub on miss and storing it for
        /// reuse. Fetches are coalesced per author and globally bounded by
        /// <see cref="AvatarCache"/>, so filling the installed list doesn't
        /// storm the Hub. Decorative: a failure just leaves the empty tile.
        /// </summary>
        private async Task AttachCachedItemAvatarAsync(ModelItem item)
        {
            var avatar = await AvatarCache.GetOrFetchAsync(AuthorOf(item.RepoName ?? item.Name));
            if (avatar is not null) item.Logo = avatar;
        }

        /// <summary>The author/org part of a repo id ("" when there is none).</summary>
        private static string AuthorOf(string repoId)
        {
            var sep = repoId.LastIndexOf('/');
            return sep > 0 ? repoId[..sep] : "";
        }

        /// <summary>
        /// Fired by a Hub search-result row's download button: builds a
        /// <see cref="ModelItem"/> for the repo and hands it to the shared
        /// download pipeline (<see cref="StartRecommendedDownloadAsync"/>) —
        /// the same disk-space/memory preflights, progress ring, cancel/pause
        /// affordances and post-download auto-load as a catalog download.
        /// The row carries the bare repo id (no quant suffix): the running
        /// server resolves its own default GGUF variant, the same rule
        /// <see cref="LlamaManager.DownloadModelAsync"/> documents for
        /// quant-less ids. The live progress shows on the model's row in the
        /// installed list; this row flips to a static checkmark.
        /// </summary>
        private void HubModelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not HubModelItemViewModel vm)
                return;
            if (vm.DownloadStarted) return; // already downloading / installed
            if (vm.IsLoadMoreRow) return; // the synthetic row has no repo to download

            var item = new ModelItem
            {
                Name = vm.DisplayName,
                RepoName = vm.RepoId,
                Description = "",
                Parameters = "",
                Size = "",
                License = "",
                Vision = false,
                Downloadable = true,
                Brand = vm.Author,
            };
            vm.DownloadStarted = true;
            _hubDownloads[vm] = item;
            _ = StartRecommendedDownloadAsync(item, fe);

            // Fetch + store the author's Hub avatar (disk-cached for reuse)
            // and attach it to the row when it lands — ModelItem.Logo
            // notifies, so the installed list's tile updates live.
            _ = AttachItemAvatarAsync(item);
        }

        /// <summary>
        /// Re-syncs the Hub rows' "added" state with the installed list: a row
        /// whose repo is already installed keeps its checkmark; one whose
        /// first-download was canceled before anything landed (its row was
        /// removed from the installed list) offers the download again. Runs on
        /// the UI thread (the LocalModels.CollectionChanged hook and the
        /// search completion both call it).
        /// </summary>
        private void UpdateHubRowStates()
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in LocalModels)
                installed.Add(m.RepoName ?? m.Name);

            foreach (var vm in HubResults)
            {
                if (vm.IsLoadMoreRow) continue; // synthetic row — no repo state
                var started = installed.Contains(vm.RepoId);
                if (!started &&
                    _hubDownloads.TryGetValue(vm, out var item) &&
                    !item.IsDownloading)
                {
                    // The download click's row is gone from the installed list
                    // and nothing is in flight — a canceled first-download.
                    _hubDownloads.Remove(vm);
                }
                vm.DownloadStarted = started || _hubDownloads.ContainsKey(vm);
            }
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

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
