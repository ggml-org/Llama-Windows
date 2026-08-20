using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LlamaApp.Common;
using LlamaApp.Llama;

namespace LlamaApp.Views;

/// <summary>
/// Details not already carried by <see cref="ModelItem"/>, loaded lazily when
/// the details view opens: the on-disk model size, the GGUF context
/// metadata (max context, KV dimensions), and the resolved file path (the
/// <c>llama fit-params</c> refinement needs it to probe the exact GGUF).
/// <c>null</c> fields mean "unknown" — the context selector then shows its
/// standard ladder unconstrained.
/// </summary>
public sealed record ModelRuntimeDetails(long ModelSizeBytes, GgufContextInfo? ContextInfo, string? FilePath = null);

/// <summary>
/// The shell operations <see cref="ModelItemDetailsViewModel"/> needs that it
/// must not own: navigation-ish actions (open URIs, clipboard), the existing
/// load/download/delete workflows (which live in <c>MainWindow</c> and stay
/// the single implementation — the details view is another frontend into the
/// same domain logic), and closing the details view. Implemented by the shell;
/// faked in unit tests.
/// </summary>
public interface IModelItemDetailsHost
{
    /// <summary>The port the local llama server listens on (for URLs).</summary>
    int ServerPort { get; }

    /// <summary>Starts the shell's normal load workflow for the model (the play-glyph path).</summary>
    Task LoadModelAsync(ModelItem model);

    /// <summary>Starts the shell's normal download workflow for a Hub model (the recommended-row path).</summary>
    Task DownloadAsync(ModelItem model);

    /// <summary>
    /// Confirms with the user, then deletes the model via the shell's normal
    /// delete path (single source of truth for the model list). Returns
    /// <c>true</c> when the model was deleted.
    /// </summary>
    Task<bool> DeleteAsync(ModelItem model);

    /// <summary>Copies text to the clipboard.</summary>
    void CopyText(string text);

    /// <summary>Opens an absolute URI in the system browser.</summary>
    void OpenUri(string uri);

    /// <summary>Closes the details view, returning to the model list.</summary>
    void CloseDetails();
}

/// <summary>
/// ViewModel for the model details view. References the selected
/// <see cref="ModelItem"/> directly — the row stays the single representation
/// of the model; this class adds only details-page interaction state (context
/// selection, memory estimate, busy/feedback flags) and orchestrates the
/// shell's existing workflows through <see cref="IModelItemDetailsHost"/>.
///
/// <para>Construction is cheap; expensive metadata (GGUF header read) loads
/// lazily via <see cref="InitializeAsync"/> after the view is already showing.
/// All members are touched on the UI thread, mirroring <see cref="ModelItem"/>.</para>
/// </summary>
public sealed class ModelItemDetailsViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The standard context ladder shown when the model's own maximum is
    /// unknown, and the candidate values constrained against the GGUF
    /// <c>context_length</c> when it is known. Not every model supports all of
    /// these — support is decided per option from the metadata.
    /// </summary>
    internal static readonly int[] StandardContextTokens =
        [4096, 8192, 16384, 32768, 65536, 131072, 262144];

    /// <summary>llama.cpp's default context size — the neutral preselection.</summary>
    internal const int DefaultContextTokens = 4096;

    private readonly IModelItemDetailsHost _host;
    private readonly Func<string, int?> _loadContextPreference;
    private readonly Action<string, int> _saveContextPreference;
    private readonly Func<string, CancellationToken, Task<ModelRuntimeDetails?>> _runtimeDetailsLoader;
    private readonly Func<CancellationToken, Task<ulong>> _memoryBudgetProbe;
    private readonly Func<string, int, CancellationToken, Task<bool?>> _fitParamsProbe;
    private readonly Action<Action>? _dispatchToUi;

    private CancellationTokenSource? _loadCts;
    private ContextLengthOption? _selectedContextLength;
    private bool _isLoadingDetails;
    private string? _errorMessage;
    private bool _isBusy;
    private bool _modelIdCopied;
    private bool _apiRequestCopied;
    private long _modelSizeBytes;
    private bool _openChatWhenLoaded;

    public ModelItemDetailsViewModel(
        ModelItem model,
        IModelItemDetailsHost host,
        Func<string, int?> loadContextPreference,
        Action<string, int> saveContextPreference,
        Func<string, CancellationToken, Task<ModelRuntimeDetails?>>? runtimeDetailsLoader = null,
        Func<CancellationToken, Task<ulong>>? memoryBudgetProbe = null,
        Func<string, int, CancellationToken, Task<bool?>>? fitParamsProbe = null,
        Action<Action>? dispatchToUi = null)
    {
        Model = model;
        _host = host;
        _loadContextPreference = loadContextPreference;
        _saveContextPreference = saveContextPreference;
        _runtimeDetailsLoader = runtimeDetailsLoader ?? LoadRuntimeDetailsAsync;
        _memoryBudgetProbe = memoryBudgetProbe
            ?? (token => LlamaManager.Shared.ContextMemoryBudgetAsync(token));
        _fitParamsProbe = fitParamsProbe ?? DefaultFitParamsProbe;
        _dispatchToUi = dispatchToUi;

        // Live state sync: the shared ModelItem already reflects every server
        // state change (the poller drives it), so the details page inherits
        // load/download transitions for free — re-derive action state and
        // honor a pending "open chat once loaded" request.
        Model.PropertyChanged += OnModelPropertyChanged;
    }

    /// <summary>The model whose details are shown — the same instance the list row binds.</summary>
    public ModelItem Model { get; }

    /// <summary>The canonical server model id (<c>repo:quant</c>) — the stable identity.</summary>
    public string ServerModelId => ((IModel)Model).ServerModelId;

    /// <summary>
    /// The header name: the clean row display name (no "(quant)" suffix, no
    /// parameter-count token) — the chips next to it carry both. See
    /// <see cref="ModelItem.RowDisplayName"/>.
    /// </summary>
    public string DisplayName => Model.RowDisplayName;

    // ---- Derived presentation (no copied state — straight from Model) ----

    /// <summary>True for a locally installed model; false for a Hub model that can be downloaded.</summary>
    public bool IsInstalled => !Model.Downloadable;

    /// <summary>
    /// Header subtitle: "size · license" (empty parts dropped). The parameter
    /// count is deliberately absent — the params chip next to the name
    /// carries it.
    /// </summary>
    public string HeaderSubtitle => string.Join(" · ",
        new[] { Model.Size, Model.License }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Parameter-count badge text ("" when unknown — the chip hides).</summary>
    public string ParameterBadge => Model.Parameters;

    /// <summary>Quant badge text ("" when unknown — the chip hides).</summary>
    public string QuantizationBadge => Model.Quant ?? "";

    public bool HasParameterBadge => !string.IsNullOrWhiteSpace(ParameterBadge);
    public bool HasQuantizationBadge => !string.IsNullOrWhiteSpace(QuantizationBadge);

    /// <summary>The HF repo page, derived from the repo id metadata (never the display name).</summary>
    public string RepositoryUrl => $"https://huggingface.co/{Model.RepoName}";

    public bool CanOpenRepository => !string.IsNullOrWhiteSpace(Model.RepoName);

    /// <summary>
    /// Size shown on the Delete/Download row: the catalog's display size, or
    /// the on-disk byte count once the lazy details load measured it.
    /// </summary>
    public string SizeDisplay => !string.IsNullOrWhiteSpace(Model.Size)
        ? Model.Size
        : _modelSizeBytes > 0
            ? DownloadProgressPresentation.FormatBytes(_modelSizeBytes)
            : "";

    // ---- Action availability (the ViewModel owns behavioral availability) ----

    /// <summary>Chat is offered for installed models (loads first when needed).</summary>
    public bool ChatRowVisible => IsInstalled;

    /// <summary>Chat acts only when the model is idle (not mid-download/load).</summary>
    public bool ChatActionEnabled => IsInstalled && !Model.IsDownloading && !Model.IsLoading;

    /// <summary>The chat row's label, following the model's transient state.</summary>
    public string ChatActionText => Model.IsDownloading
        ? "Downloading…"
        : Model.IsLoading
            ? "Loading model…"
            : "Chat with model";

    /// <summary>Delete is offered for installed models, and only while unloaded and idle.</summary>
    public bool CanDelete => IsInstalled && !IsBusy && Model.PlayGlyphVisible;

    /// <summary>Download is offered for Hub models not yet installed.</summary>
    public bool CanDownload => !IsInstalled && !IsBusy;

    /// <summary>
    /// The context section shows for on-disk models (GGUF-derived ladder +
    /// <c>fit-params</c> refinement) and for downloadable rows whose catalog
    /// size is known — the ladder then grays from the model size against the
    /// memory budget up front ("can the weights even load"). A Hub row
    /// without a size gets no ladder: there is nothing to check against.
    /// </summary>
    public bool ContextSectionVisible => IsInstalled || Model.SizeBytes > 0;

    // ---- Context length selection ----

    /// <summary>The candidate context lengths with per-option memory estimates.</summary>
    public ObservableCollection<ContextLengthOption> ContextLengths { get; } = [];

    /// <summary>The selected context length; drives the header memory estimate.</summary>
    public ContextLengthOption? SelectedContextLength
    {
        get => _selectedContextLength;
        private set
        {
            if (ReferenceEquals(_selectedContextLength, value)) return;
            if (_selectedContextLength is not null) _selectedContextLength.IsSelected = false;
            _selectedContextLength = value;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EstimatedMemoryDisplay));
        }
    }

    /// <summary>Estimated memory at the selected context length ("" until known).</summary>
    public string EstimatedMemoryDisplay => SelectedContextLength?.EstimatedMemoryDisplay ?? "";

    /// <summary>
    /// Selects a context length and persists it per model (applied as
    /// <c>ctx_size</c> on the next load). Unselectable options (above the
    /// model's max context, or beyond available memory) can't be selected.
    /// </summary>
    public void SelectContextLength(ContextLengthOption option)
    {
        if (!option.IsSelectable || !ContextLengths.Contains(option)) return;
        SelectedContextLength = option;
        _saveContextPreference(ServerModelId, option.Tokens);
    }

    // ---- Lazy details loading ----

    /// <summary>True while the GGUF metadata is being read (context section shows a ring).</summary>
    public bool IsLoadingDetails
    {
        get => _isLoadingDetails;
        private set { if (_isLoadingDetails == value) return; _isLoadingDetails = value; OnPropertyChanged(); }
    }

    /// <summary>A single generic details-load error; null when all is well.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>True when <see cref="ErrorMessage"/> is set (drives the error line's visibility).</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>True while a delete/download request is in flight (actions disable).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanDownload));
        }
    }

    /// <summary>Brief check-mark feedback after "Copy model ID".</summary>
    public bool ModelIdCopied
    {
        get => _modelIdCopied;
        private set { if (_modelIdCopied == value) return; _modelIdCopied = value; OnPropertyChanged(); }
    }

    /// <summary>Brief check-mark feedback after "Build an API request".</summary>
    public bool ApiRequestCopied
    {
        get => _apiRequestCopied;
        private set { if (_apiRequestCopied == value) return; _apiRequestCopied = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Loads the details not already carried by <see cref="ModelItem"/> — the
    /// GGUF context metadata read from the on-disk file — then builds the
    /// context options and restores the persisted per-model selection. Runs
    /// after the view is already showing; cancel-safe (navigating away
    /// cancels via <see cref="Dispose"/>).
    ///
    /// <para>Downloadable rows take the same path with no local file: the
    /// loader is skipped, the ladder falls back to the catalog size, and the
    /// options are grayed purely from the memory budget. Once the options
    /// are visible, an on-disk model's fit is re-checked per option with
    /// <c>llama fit-params</c> (which knows the real devices and free
    /// memory), refining the heuristic graying in place as verdicts land.</para>
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancel = default)
    {
        _loadCts?.Cancel();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var token = _loadCts.Token;

        IsLoadingDetails = true;
        ErrorMessage = null;
        ModelRuntimeDetails? details = null;
        try
        {
            // A Hub row has no local file to interrogate — the catalog size
            // stands in for the weights and the budget decides the graying.
            if (IsInstalled)
            {
                details = await _runtimeDetailsLoader(ServerModelId, token);
                token.ThrowIfCancellationRequested();
            }

            _modelSizeBytes = details?.ModelSizeBytes ?? (long)Model.SizeBytes;
            RebuildContextOptions(details, await ProbeMemoryBudgetAsync(token));
            RestoreSelection();
            OnPropertyChanged(nameof(SizeDisplay));
        }
        catch (OperationCanceledException)
        {
            // Navigated away (or a newer InitializeAsync superseded this one).
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "model details load failed: " + ServerModelId);
            ErrorMessage = "Couldn't load the model's details.";
        }
        finally
        {
            if (!token.IsCancellationRequested) IsLoadingDetails = false;
        }

        // Options are on screen: ask llama fit-params for the ground truth on
        // each option's memory fit (real KV dtype, compute buffers, actual
        // devices) and refine the heuristic graying as verdicts land.
        if (details?.FilePath is { } path && !token.IsCancellationRequested)
        {
            try
            {
                await RefineContextFitsAsync(path, token);
            }
            catch (OperationCanceledException)
            {
                // Navigated away mid-refinement — the heuristic graying stands.
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "fit-params refinement failed: " + ServerModelId);
            }
        }
    }

    /// <summary>
    /// The memory budget the ladder is grayed against (free VRAM + usable
    /// free RAM — see <see cref="LlamaManager.ContextMemoryBudgetAsync"/>).
    /// Fails open: an unknown budget grays nothing out, matching every other
    /// probe in the app.
    /// </summary>
    private async Task<ulong> ProbeMemoryBudgetAsync(CancellationToken token)
    {
        try
        {
            return await _memoryBudgetProbe(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"memory budget probe failed: {ex.Message}");
            return ulong.MaxValue;
        }
    }

    /// <summary>
    /// The default fit-params probe: the manager's cached
    /// <c>llama fit-params</c> estimate for the model at
    /// <paramref name="contextTokens"/>, compared against the same memory
    /// budget the ladder used. Tri-state: <c>null</c> when the tool has no
    /// verdict (missing binary, CPU-era build, unreadable model) — the
    /// option keeps its heuristic graying then.
    /// </summary>
    private static async Task<bool?> DefaultFitParamsProbe(
        string modelPath, int contextTokens, CancellationToken token)
    {
        var manager = LlamaManager.Shared;
        var estimate = await manager.QueryFitParamsAsync(modelPath, contextTokens, token);
        if (estimate is null) return null;

        var budget = await manager.ContextMemoryBudgetAsync(token);
        return budget == ulong.MaxValue || estimate.TotalBytes <= budget;
    }

    /// <summary>
    /// Runs the fit-params probe for every supported option of an on-disk
    /// model, sequentially (each probe spawns a short-lived CLI process;
    /// a burst of seven is fine, a burst of seven-at-once is not), flipping
    /// <see cref="ContextLengthOption.FitsInMemory"/> where llama.cpp's own
    /// accounting disagrees with the heuristic. Unsupported options
    /// (beyond the model's max context) are skipped — that guard wins
    /// regardless of memory. A probe failure or a missing verdict leaves
    /// the option's heuristic state untouched.
    /// </summary>
    private async Task RefineContextFitsAsync(string modelPath, CancellationToken token)
    {
        foreach (var option in ContextLengths.ToList())
        {
            token.ThrowIfCancellationRequested();
            if (!option.IsSupported) continue;

            bool? fits;
            try
            {
                fits = await _fitParamsProbe(modelPath, option.Tokens, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"fit-params probe threw for ctx {option.Tokens}: {ex.Message}");
                continue;
            }

            if (fits is not { } verdict || option.FitsInMemory == verdict) continue;

            // The probe awaits a child process; the continuation can land on
            // a thread-pool thread — flip the bound property on the UI thread.
            if (_dispatchToUi is null)
                option.FitsInMemory = verdict;
            else
                _dispatchToUi(() => option.FitsInMemory = verdict);
        }
    }

    /// <summary>
    /// Builds the context ladder: the standard token values, each flagged
    /// unsupported above the model's max context, each carrying its memory
    /// estimate (weights + KV cache) and a fit check against the machine's
    /// memory budget (<paramref name="budgetBytes"/> — free VRAM plus the
    /// usable share of free RAM, since a load can split across them). With
    /// no GGUF metadata (downloadable rows, unreadable header) every option
    /// is supported and shows the bare model size — the graying then answers
    /// "can the weights even load".
    /// </summary>
    internal static List<ContextLengthOption> BuildOptions(
        ModelRuntimeDetails? details, long modelSizeBytes, ulong budgetBytes = ulong.MaxValue)
    {
        var info = details?.ContextInfo;
        var size = details?.ModelSizeBytes > 0 ? details.ModelSizeBytes : modelSizeBytes;
        return StandardContextTokens.Select(tokens =>
        {
            var estimate = info is not null
                ? ContextMemoryEstimate.EstimateTotalBytes(size, tokens, info)
                : size;
            return new ContextLengthOption
            {
                Tokens = tokens,
                Label = ContextLengthOption.FormatTokenCount(tokens),
                MaxContextTokens = info?.ContextLength ?? 0,
                EstimatedMemoryBytes = estimate,
                IsSupported = info is null || tokens <= info.ContextLength,
                FitsInMemory = estimate <= 0 || (ulong)estimate <= budgetBytes,
            };
        }).ToList();
    }

    private void RebuildContextOptions(ModelRuntimeDetails? details, ulong budgetBytes)
    {
        ContextLengths.Clear();
        foreach (var option in BuildOptions(details, _modelSizeBytes, budgetBytes))
            ContextLengths.Add(option);
    }

    /// <summary>
    /// Restores the persisted per-model preference when it's still supported;
    /// otherwise falls back to the llama.cpp default (clamped to what the
    /// model supports) — never silently the largest context.
    /// </summary>
    private void RestoreSelection()
    {
        var pref = _loadContextPreference(ServerModelId);
        var restored = pref is { } p
            ? ContextLengths.FirstOrDefault(o => o.Tokens == p && o.IsSelectable)
            : null;
        SelectedContextLength = restored
            ?? ContextLengths.FirstOrDefault(o => o.Tokens == DefaultContextTokens && o.IsSelectable)
            ?? ContextLengths.LastOrDefault(o => o.IsSelectable)
            ?? ContextLengths.FirstOrDefault();
    }

    /// <summary>
    /// The default runtime-details loader: takes the model's on-disk path from
    /// the manager's latest /models snapshot when the server reports one
    /// (falling back to a fresh fetch), otherwise locates the GGUF in the
    /// local download caches (<see cref="ModelFileLocator"/> — the router's
    /// /models payload carries no path), then reads the GGUF header. Returns
    /// null when the model has no findable local file.
    /// </summary>
    internal static async Task<ModelRuntimeDetails?> LoadRuntimeDetailsAsync(
        string serverModelId, CancellationToken cancel)
    {
        var mgr = LlamaManager.Shared;
        string? path = null;
        long size = 0;

        if (mgr.ServerStatus == LlamaManager.ServerState.Running)
        {
            var sm = FindServerModel(mgr.LastModelsSnapshot, serverModelId)
                     ?? FindServerModel(await mgr.GetModelsAsync(cancel), serverModelId);
            if (sm?.Path is { } serverPath)
            {
                path = serverPath;
                try { size = new FileInfo(path).Length; }
                catch (Exception ex) { Log.Debug("model file size unreadable: " + ex.Message); }
            }
        }

        if (path is null)
        {
            var located = ModelFileLocator.TryFind(serverModelId);
            if (located is null) return null;
            path = located.PrimaryFilePath;
            size = located.TotalSizeBytes;
        }

        var info = await GgufMetadata.ReadContextInfoAsync(path, cancel);
        return new ModelRuntimeDetails(size, info, path);
    }

    /// <summary>
    /// Finds a server model by canonical id, tolerating the bare-repo id the
    /// server reports while a download is mid-flight (quant unresolved).
    /// </summary>
    private static LlamaManager.ServerModel? FindServerModel(
        IReadOnlyList<LlamaManager.ServerModel> models, string serverModelId)
    {
        var repo = serverModelId.Split(':')[0];
        return models.FirstOrDefault(m =>
            string.Equals(m.Id, serverModelId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Id.Split(':')[0], repo, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Actions (orchestrate the shell's existing workflows) ----

    /// <summary>
    /// Chat with the model: opens the server's WebUI when the model is loaded;
    /// otherwise starts the shell's normal load workflow and opens the chat
    /// once the poller reports the model resident.
    /// </summary>
    public async Task ChatAsync()
    {
        if (!ChatActionEnabled) return;
        if (Model.IsLoaded)
        {
            OpenChat();
            return;
        }

        _openChatWhenLoaded = true;
        await _host.LoadModelAsync(Model);
    }

    /// <summary>Copies the canonical server model id (<c>repo:quant</c>).</summary>
    public void CopyModelId()
    {
        _host.CopyText(ServerModelId);
        FlashCopied(isModelId: true);
    }

    /// <summary>
    /// Copies a ready-to-run curl command for the server's chat endpoint,
    /// built from the same model id and port the app itself uses.
    /// </summary>
    public void BuildApiRequest()
    {
        _host.CopyText(ApiRequestPresentation.BuildCurlCommand(_host.ServerPort, ServerModelId));
        FlashCopied(isModelId: false);
    }

    /// <summary>Opens the model's Hugging Face repo page.</summary>
    public void OpenRepository()
    {
        if (CanOpenRepository) _host.OpenUri(RepositoryUrl);
    }

    /// <summary>Starts the shell's download workflow for a Hub model.</summary>
    public async Task DownloadAsync()
    {
        if (!CanDownload || IsBusy) return;
        IsBusy = true;
        try { await _host.DownloadAsync(Model); }
        finally { IsBusy = false; }
    }

    /// <summary>Asks the shell to confirm + delete the model from disk.</summary>
    public async Task DeleteAsync()
    {
        if (!CanDelete || IsBusy) return;
        IsBusy = true;
        try { await _host.DeleteAsync(Model); }
        finally { IsBusy = false; }
    }

    private void OpenChat()
        => _host.OpenUri(ApiRequestPresentation.BuildWebUiUrl(_host.ServerPort, ServerModelId));

    /// <summary>
    /// Re-derives action state when the shared ModelItem changes (the poller
    /// and the shell's drivers update it continuously), and opens the pending
    /// chat once a requested load lands.
    /// </summary>
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshActionState();
        if (_openChatWhenLoaded && Model.LoadFailed)
            _openChatWhenLoaded = false; // the load was rejected — don't open later
        if (_openChatWhenLoaded && Model.IsLoaded)
        {
            _openChatWhenLoaded = false;
            OpenChat();
        }
    }

    /// <summary>Raises change notifications for every Model-derived action property.</summary>
    private void RefreshActionState()
    {
        OnPropertyChanged(nameof(ChatActionEnabled));
        OnPropertyChanged(nameof(ChatActionText));
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>Sets a copied-feedback flag briefly, then clears it (fire-and-forget).</summary>
    private async void FlashCopied(bool isModelId)
    {
        if (isModelId) ModelIdCopied = true; else ApiRequestCopied = true;
        try { await Task.Delay(1500); } catch { return; }
        if (isModelId) ModelIdCopied = false; else ApiRequestCopied = false;
    }

    /// <summary>Cancels any in-flight details load and detaches from the model.</summary>
    public void Dispose()
    {
        Model.PropertyChanged -= OnModelPropertyChanged;
        _loadCts?.Cancel();
        _loadCts = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
