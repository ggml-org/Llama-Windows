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

        public MainWindow()
        {
            InitializeComponent();
            ConfigureAsFlyout();
            Closed += MainWindow_Closed;
            Activated += MainWindow_Activated;

            LoadModels();
            LoadVersionInfo();
            UpdateEmptyState();

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
            _ = LoadRecommendedModelsAsync();
            _ = LoadLocalModelsAsync();
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
                Name = DeriveDisplayName(repo, quant, byRepo),
                RepoName = repo,
                Quant = quant,
                Parameters = matched?.Parameters ?? "",
                Size = matched?.Size ?? "",
                License = matched?.License ?? "",
                Vision = sm.SupportsImage, // authoritative — from the server
                Downloadable = false,
                Brand = matched?.Brand,
                Logo = ModelItem.ResolveLogo(matched?.Brand),
                IsLoaded = sm.IsLoaded,
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
        /// Fetches the remote catalog and populates <see cref="RecommendedModels"/>.
        /// Shares the single catalog fetch with the Available section.
        /// </summary>
        private async Task LoadRecommendedModelsAsync()
        {
            List<Repository> repos;
            try { repos = await _catalogTask; }
            catch (Exception ex) { Log.Warn(ex, "recommended models load failed"); return; } // network/parse failure — Recommended stays empty

            // Build a display name that disambiguate quants: "GPT-OSS 20B (mxfp4)".
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

        /// <summary>
        /// Shows/hides the "No model yet." placeholder based on whether any
        /// local models are present.
        /// </summary>
        private void UpdateEmptyState()
        {
            var empty = LocalModels.Count == 0;
            NoLocalModelsText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LocalModelsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- Model download + launch ----

        /// <summary>
        /// Fired when a row in the Recommended Models section is tapped. Moves
        /// the model to the Available section with a progress ring, kicks off
        /// <see cref="LlamaManager.DownloadModelAsync"/> via the running llama
        /// server, then loads it (see <see cref="LoadAndWatchAsync"/>) when the
        /// download completes — the row transitions download ring -> load ring ->
        /// OpenInNewWindow glyph.
        /// </summary>
        private void RecommendedModel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
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
                    queue.TryEnqueue(Apply);
            });

            try
            {
                var ok = await mgr.DownloadModelAsync(item, progress);
                void Complete()
                {
                    item.IsDownloading = false;
                    if (ok)
                    {
                        // Download done — load it. The row now shows the
                        // indeterminate load ring until the poller reports the
                        // model as loaded.
                        item.IsLoading = true;
                        _ = LoadAndWatchAsync(item);
                    }
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
            item.IsLoading = true;
            _ = LoadAndWatchAsync(item);
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
            
            var url = $"http://localhost:{LlamaManager.ServerPort}?model={Uri.EscapeDataString(serverModelId)}";
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
        /// the row falls back to the play glyph. On acceptance the load ring
        /// stays up until the <see cref="LlamaManager.ModelsChanged"/> poller
        /// reports the model as <c>loaded</c> (which sets <see cref="ModelItem.IsLoaded"/>
        /// and clears <see cref="ModelItem.IsLoading"/> via <see cref="ReconcileAsync"/>).
        /// </summary>
        private async Task LoadAndWatchAsync(ModelItem item)
        {
            var mgr = LlamaManager.Shared;
            var queue = DispatcherQueue;
            var ok = await mgr.LoadModelAsync(item);
            if (!ok)
            {
                if (queue is null || queue.HasThreadAccess)
                    item.IsLoading = false;
                else
                    queue.TryEnqueue(() => item.IsLoading = false);
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
                    if (queue is null || queue.HasThreadAccess)
                        item.IsLoading = false;
                    else
                        queue.TryEnqueue(() => { if (item.IsLoading && !item.IsLoaded) item.IsLoading = false; });
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
                if (_localByServerId.TryGetValue(sm.Id, out var item))
                {
                    // Map the server's three load states onto the row:
                    //   loaded  -> OpenInNewWindow glyph (IsLoaded, ring off)
                    //   loading -> indeterminate load ring (server-truth load)
                    //   unloaded-> play glyph (but don't clobber an optimistic
                    //              IsLoading set by a just-fired play click that
                    //              the server hasn't acknowledged yet)
                    if (sm.IsLoaded)
                    {
                        if (!item.IsLoaded) Log.Info("model loaded: " + sm.Id);
                        item.IsLoaded = true;
                        item.IsLoading = false;
                    }
                    else if (sm.IsLoading)
                    {
                        if (!item.IsLoading) Log.Info("model loading: " + sm.Id);
                        item.IsLoaded = false;
                        item.IsLoading = true;
                    }
                    else // "unloaded" (or unknown)
                    {
                        if (item.IsLoaded) Log.Info("model unloaded: " + sm.Id);
                        item.IsLoaded = false;
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

            UpdateEmptyState();
        }

        // ---- Footer actions ----

        private async void ServerLink_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Open the running llama server's WebUI in the system browser.
            await Windows.System.Launcher.LaunchUriAsync(
                new System.Uri($"http://localhost:{LlamaManager.ServerPort}"));
        }

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
