using LlamaApp.Llama;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the model details ViewModel
/// (<see cref="ModelItemDetailsViewModel"/>). The rules that matter: the
/// context ladder is data-driven from the GGUF metadata (unsupported options
/// can't be selected), the per-model selection persists and is restored
/// (never silently the largest context), every action delegates to the
/// shell's existing workflows via <see cref="IModelItemDetailsHost"/>, and
/// the action state tracks the shared <see cref="ModelItem"/> live. The host
/// and the runtime-details loader are faked — no server, no disk, no
/// <c>Settings</c> file (a real <c>Settings.Save()</c> would touch the user's
/// actual settings.json).
/// </summary>
public sealed class ModelItemDetailsViewModelTests
{
    // ---- Fakes ----

    private sealed class FakeHost : IModelItemDetailsHost
    {
        public int ServerPort => 9931;
        public List<string> CopiedTexts { get; } = [];
        public List<string> OpenedUris { get; } = [];
        public List<ModelItem> Loads { get; } = [];
        public List<ModelItem> Downloads { get; } = [];
        public List<ModelItem> Deletes { get; } = [];
        public int CloseDetailsCalls;

        public Task LoadModelAsync(ModelItem model) { Loads.Add(model); return Task.CompletedTask; }
        public Task DownloadAsync(ModelItem model) { Downloads.Add(model); return Task.CompletedTask; }
        public Task<bool> DeleteAsync(ModelItem model) { Deletes.Add(model); return Task.FromResult(true); }
        public void CopyText(string text) => CopiedTexts.Add(text);
        public void OpenUri(string uri) => OpenedUris.Add(uri);
        public void CloseDetails() => CloseDetailsCalls++;
    }

    /// <summary>In-memory stand-in for Settings.ModelContextLengths.</summary>
    private sealed class FakePreferences
    {
        public Dictionary<string, int> Saved { get; } = [];
        public int? Load(string id) => Saved.TryGetValue(id, out var t) ? t : null;
        public void Save(string id, int tokens) => Saved[id] = tokens;
    }

    /// <summary>Llama-3-8B-shaped runtime details (max context 32k here, to
    /// exercise the unsupported-option path against the standard ladder).</summary>
    private static readonly ModelRuntimeDetails Details32K = new(
        ModelSizeBytes: 4_000_000_000L,
        ContextInfo: new GgufContextInfo("llama", 32768, 32, 4096, 32, 8, 128));

    private static ModelItem InstalledItem() => new()
    {
        Name = "Qwen3-4B",
        RepoName = "unsloth/Qwen3-4B-GGUF",
        Quant = "Q4_K_M",
        Parameters = "4B",
        Size = "2.5 GB",
        License = "Apache 2.0",
    };

    private static ModelItem HubItem() => new()
    {
        Name = "Qwen3-4B",
        RepoName = "unsloth/Qwen3-4B-GGUF",
        Quant = "Q4_K_M",
        Size = "2.5 GB",
        Downloadable = true,
    };

    private static ModelItemDetailsViewModel MakeVm(
        ModelItem item, FakeHost host, FakePreferences prefs,
        Func<ulong>? memoryBudget = null)
        => MakeVm(item, host, prefs, Details32K, memoryBudget);

    private static ModelItemDetailsViewModel MakeVm(
        ModelItem item,
        FakeHost host,
        FakePreferences prefs,
        ModelRuntimeDetails? details,
        Func<ulong>? memoryBudget = null,
        Func<string, int, CancellationToken, Task<bool?>>? fitParamsProbe = null,
        Action<Action>? dispatchToUi = null)
        => new(item, host, prefs.Load, prefs.Save,
            runtimeDetailsLoader: (_, _) => Task.FromResult(details),
            // Deterministic by default: an unbounded budget, everything fits.
            // No FilePath on the fixture details, so the fit-params
            // refinement stays out of the way unless a test opts in.
            memoryBudgetProbe: _ => Task.FromResult(memoryBudget is null ? ulong.MaxValue : memoryBudget()),
            fitParamsProbe: fitParamsProbe,
            dispatchToUi: dispatchToUi);

    // ---- Construction / derived presentation ----

    [Fact]
    public void Construction_Is_Cheap_And_Derives_Everything_From_The_Model()
    {
        var item = InstalledItem();
        var vm = MakeVm(item, new FakeHost(), new FakePreferences());

        Assert.Same(item, vm.Model);
        Assert.Equal("unsloth/Qwen3-4B-GGUF:Q4_K_M", vm.ServerModelId);
        // Params are carried by the chip next to the name, not the subtitle.
        Assert.Equal("2.5 GB · Apache 2.0", vm.HeaderSubtitle);
        Assert.Equal("4B", vm.ParameterBadge);
        Assert.Equal("Q4_K_M", vm.QuantizationBadge);
        Assert.Equal("https://huggingface.co/unsloth/Qwen3-4B-GGUF", vm.RepositoryUrl);
        Assert.True(vm.IsInstalled);
        Assert.True(vm.ContextSectionVisible);
        // Nothing loaded yet — the lazy load is InitializeAsync's job.
        Assert.Empty(vm.ContextLengths);
        Assert.False(vm.IsLoadingDetails);
    }

    [Fact]
    public void The_Header_Name_Drops_The_Parenthesized_Quant_The_Chip_Carries_It()
    {
        // The catalog suffixes installed names with "(quant)" to disambiguate
        // quants in the list; the details header has a dedicated quant chip.
        var item = InstalledItem();
        item.Name = "Gemma 3 1B (Q4_0)";
        item.Quant = "Q4_0";
        var vm = MakeVm(item, new FakeHost(), new FakePreferences());

        Assert.Equal("Gemma 3 1B", vm.DisplayName);
        Assert.Equal("Q4_0", vm.QuantizationBadge);
    }

    [Fact]
    public void The_Header_Name_Drops_The_Parameters_Token_The_Chip_Carries_It()
    {
        var item = InstalledItem();
        item.Name = "Gemma 3 1B (Q4_0)";
        item.Quant = "Q4_0";
        item.Parameters = "1B";
        var vm = MakeVm(item, new FakeHost(), new FakePreferences());

        Assert.Equal("Gemma 3", vm.DisplayName);
        Assert.Equal("1B", vm.ParameterBadge);
    }

    [Fact]
    public void The_Header_Name_Keeps_Names_Without_A_Matching_Quant_Suffix()
    {
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences());

        Assert.Equal("Qwen3-4B", vm.DisplayName);
    }

    [Fact]
    public void Hub_Rows_Hide_The_Installed_Only_Sections()
    {
        var vm = MakeVm(HubItem(), new FakeHost(), new FakePreferences());

        Assert.False(vm.IsInstalled);
        Assert.False(vm.ContextSectionVisible);
        Assert.False(vm.ChatRowVisible);
        Assert.True(vm.CanDownload);
        Assert.False(vm.CanDelete);
    }

    // ---- Context ladder ----

    [Fact]
    public async Task InitializeAsync_Builds_The_Ladder_Constrained_By_The_Models_Max_Context()
    {
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences());
        await vm.InitializeAsync();

        Assert.Equal(
            [4096, 8192, 16384, 32768, 65536, 131072, 262144],
            vm.ContextLengths.Select(o => o.Tokens).ToArray());
        // 32k max: everything above is present but unsupported.
        Assert.Equal(
            [true, true, true, true, false, false, false],
            vm.ContextLengths.Select(o => o.IsSupported).ToArray());
        // The llama.cpp default is preselected — never silently the largest.
        Assert.Equal(4096, vm.SelectedContextLength?.Tokens);
        Assert.True(vm.ContextLengths[0].IsSelected);
    }

    [Fact]
    public async Task InitializeAsync_Attaches_A_Memory_Estimate_To_Every_Option()
    {
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences());
        await vm.InitializeAsync();

        // 4 GB weights + 512 MiB KV at 4k (see ContextMemoryEstimateTests).
        Assert.Equal(4_000_000_000L + 512L * 1024 * 1024,
            vm.ContextLengths[0].EstimatedMemoryBytes);
        // 32k: 8× the KV of 4k.
        Assert.Equal(4_000_000_000L + 8 * 512L * 1024 * 1024,
            vm.ContextLengths[3].EstimatedMemoryBytes);
        Assert.False(string.IsNullOrEmpty(vm.EstimatedMemoryDisplay));
    }

    [Fact]
    public async Task InitializeAsync_Without_Metadata_Offers_The_Full_Ladder_Unconstrained()
    {
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(), details: null);
        await vm.InitializeAsync();

        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSupported));
        Assert.All(vm.ContextLengths, o => Assert.True(o.FitsInMemory));
        Assert.All(vm.ContextLengths, o => Assert.Equal("", o.TooltipText));
        Assert.Equal(4096, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task Options_That_Exceed_Available_Memory_Are_Disabled_With_A_Requires_Memory_Tooltip()
    {
        // A 256k-max model so the context guard stays out of the way: only
        // the memory guard can disable. Estimates: 4 GB weights + 128 KiB of
        // KV per token — 32k needs ~8.3 GB, 64k ~12.6 GB.
        var details256K = new ModelRuntimeDetails(
            ModelSizeBytes: 4_000_000_000L,
            ContextInfo: new GgufContextInfo("llama", 262144, 32, 4096, 32, 8, 128));
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details256K, memoryBudget: () => 9_000_000_000UL);
        await vm.InitializeAsync();

        Assert.Equal(
            [true, true, true, true, false, false, false],
            vm.ContextLengths.Select(o => o.FitsInMemory).ToArray());
        Assert.Equal(
            [true, true, true, true, false, false, false],
            vm.ContextLengths.Select(o => o.IsSelectable).ToArray());
        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSupported));

        var tooltip = vm.ContextLengths[4].TooltipText; // 64k
        Assert.StartsWith("Model requires at least ", tooltip);
        Assert.EndsWith(" of memory", tooltip);
        Assert.Contains("GB", tooltip);

        var enabled = vm.ContextLengths[0].TooltipText; // 4k fits
        Assert.StartsWith("Requires ", enabled);
        Assert.EndsWith(" of memory", enabled);

        // And a memory-capped option can't be selected.
        vm.SelectContextLength(vm.ContextLengths[4]);
        Assert.Equal(4096, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task Downloadable_Models_Show_The_Ladder_Grayed_From_The_Catalog_Size()
    {
        // A Hub row with a known size gets the ladder (no GGUF header yet —
        // every option shows the bare weights) and is grayed out of the box
        // when the weights fit nowhere: the budget is VRAM + usable RAM, so
        // a 20 GB model is fine on a 16 GB GPU + 8 GB usable RAM machine…
        var hub = HubItem();
        hub.SizeBytes = 20_000_000_000UL;
        var vm = MakeVm(hub, new FakeHost(), new FakePreferences(),
            details: null, memoryBudget: () => 24_000_000_000UL);
        await vm.InitializeAsync();

        Assert.True(vm.ContextSectionVisible);
        Assert.Equal(7, vm.ContextLengths.Count);
        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSupported));
        Assert.All(vm.ContextLengths, o => Assert.True(o.FitsInMemory));
        Assert.All(vm.ContextLengths, o => Assert.Equal(20_000_000_000L, o.EstimatedMemoryBytes));
    }

    [Fact]
    public async Task Downloadable_Models_Whose_Weights_Fit_Nowhere_Gray_Every_Option()
    {
        // …but grayed entirely when the weights alone exceed the budget —
        // "can this model even load here", answered before the download.
        var hub = HubItem();
        hub.SizeBytes = 40_000_000_000UL;
        var vm = MakeVm(hub, new FakeHost(), new FakePreferences(),
            details: null, memoryBudget: () => 24_000_000_000UL);
        await vm.InitializeAsync();

        Assert.True(vm.ContextSectionVisible);
        Assert.All(vm.ContextLengths, o => Assert.False(o.FitsInMemory));
        Assert.All(vm.ContextLengths, o => Assert.False(o.IsSelectable));
        Assert.All(vm.ContextLengths, o => Assert.StartsWith("Model requires at least ", o.TooltipText));
    }

    [Fact]
    public async Task InitializeAsync_Refines_Option_Fit_With_FitParams_Verdicts()
    {
        // Heuristic budget is unbounded (everything fits), then fit-params
        // says only 16k and below actually fit — the 32k option must flip
        // to grayed. Unsupported options (64k+, above the 32k max) are
        // never probed.
        var probed = new List<int>();
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details: Details32K with { FilePath = "/fake/model.gguf" },
            fitParamsProbe: (_, tokens, _) =>
            {
                probed.Add(tokens);
                return Task.FromResult<bool?>(tokens <= 16384);
            });
        await vm.InitializeAsync();

        Assert.Equal([4096, 8192, 16384, 32768], probed);
        Assert.Equal(
            [true, true, true, false, true, true, true],
            vm.ContextLengths.Select(o => o.FitsInMemory).ToArray());
        Assert.False(vm.ContextLengths[3].IsSelectable);
    }

    [Fact]
    public async Task InitializeAsync_Keeps_Heuristic_Graying_When_FitParams_Has_No_Verdict()
    {
        // A null verdict (no binary, CPU-era build, unreadable model) keeps
        // the heuristic: a budget-grayed option stays grayed, a fitting one
        // stays enabled — fit-params never overrides with "unknown".
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details: Details32K with { FilePath = "/fake/model.gguf" },
            memoryBudget: () => 5_000_000_000UL,
            fitParamsProbe: (_, _, _) => Task.FromResult<bool?>(null));
        await vm.InitializeAsync();

        // 4k (~4.5 GB) fits the 5 GB budget, 8k (~5.1 GB) and up don't.
        Assert.Equal(
            [true, false, false, false, false, false, false],
            vm.ContextLengths.Select(o => o.FitsInMemory).ToArray());
    }

    [Fact]
    public async Task FitParams_Refinement_Flips_Options_Through_The_Ui_Dispatcher()
    {
        // Verdicts land from a process await — the ViewModel must not touch
        // bound properties directly when a dispatcher is provided; running
        // the queued actions applies the flip (with change notifications).
        var dispatched = new List<Action>();
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details: Details32K with { FilePath = "/fake/model.gguf" },
            fitParamsProbe: (_, tokens, _) => Task.FromResult<bool?>(tokens <= 8192),
            dispatchToUi: dispatched.Add);
        await vm.InitializeAsync();

        var option = vm.ContextLengths[2]; // 16k — fits the heuristic, fails fit-params
        Assert.True(option.FitsInMemory); // queued, not applied yet
        Assert.NotEmpty(dispatched);

        var raised = new List<string?>();
        option.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        dispatched.ForEach(a => a());

        Assert.False(option.FitsInMemory);
        Assert.False(option.IsSelectable);
        Assert.Contains("FitsInMemory", raised);
        Assert.Contains("IsSelectable", raised);
        Assert.Contains("TooltipText", raised);
    }

    [Fact]
    public async Task Context_Capped_Options_Explain_The_Cap_On_Hover()
    {
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences());
        await vm.InitializeAsync();

        var capped = vm.ContextLengths[4]; // 64k > the model's 32k max
        Assert.False(capped.IsSupported);
        Assert.True(capped.FitsInMemory);
        Assert.False(capped.IsSelectable);
        Assert.Equal("This model supports up to 32k of context", capped.TooltipText);
    }

    [Fact]
    public async Task RestoreSelection_Skips_A_Memory_Capped_Preference()
    {
        // ~5 GB free: 4k fits (~4.5 GB), 8k (~5.1 GB) and up don't — the
        // persisted 16k preference must fall back to the 4k default.
        var prefs = new FakePreferences();
        prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"] = 16384;
        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs,
            memoryBudget: () => 5_000_000_000UL);
        await vm.InitializeAsync();

        Assert.Equal(4096, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task InitializeAsync_Restores_The_Persisted_Per_Model_Selection()
    {
        var prefs = new FakePreferences();
        prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"] = 16384;

        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs);
        await vm.InitializeAsync();

        Assert.Equal(16384, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task InitializeAsync_Drops_A_Persisted_Selection_The_Model_Cant_Support()
    {
        var prefs = new FakePreferences();
        prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"] = 131072; // above the 32k max

        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs);
        await vm.InitializeAsync();

        Assert.Equal(4096, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task Selecting_A_Context_Persists_It_Per_Model_And_Updates_The_Estimate()
    {
        var prefs = new FakePreferences();
        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs);
        await vm.InitializeAsync();
        var before = vm.EstimatedMemoryDisplay;

        vm.SelectContextLength(vm.ContextLengths[2]); // 16k

        Assert.Equal(16384, prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"]);
        Assert.Equal(16384, vm.SelectedContextLength?.Tokens);
        Assert.False(vm.ContextLengths[0].IsSelected);
        Assert.True(vm.ContextLengths[2].IsSelected);
        Assert.NotEqual(before, vm.EstimatedMemoryDisplay);
    }

    [Fact]
    public async Task Unsupported_Options_Cant_Be_Selected()
    {
        var prefs = new FakePreferences();
        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs);
        await vm.InitializeAsync();

        vm.SelectContextLength(vm.ContextLengths[4]); // 64k > 32k max

        Assert.Equal(4096, vm.SelectedContextLength?.Tokens);
        Assert.Empty(prefs.Saved);
    }

    [Fact]
    public async Task The_Preference_Is_Keyed_Per_Model()
    {
        var prefs = new FakePreferences();
        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs);
        await vm.InitializeAsync();
        vm.SelectContextLength(vm.ContextLengths[1]); // 8k

        // A different model (different quant → different server id) is unaffected.
        var other = InstalledItem();
        other.Quant = "Q8_0";
        var vm2 = MakeVm(other, new FakeHost(), prefs);
        await vm2.InitializeAsync();

        Assert.Equal(4096, vm2.SelectedContextLength?.Tokens);
    }

    // ---- Actions ----

    [Fact]
    public async Task Chat_Opens_The_WebUI_When_The_Model_Is_Already_Loaded()
    {
        var host = new FakeHost();
        var item = InstalledItem();
        item.IsLoaded = true;
        var vm = MakeVm(item, host, new FakePreferences());

        await vm.ChatAsync();

        Assert.Empty(host.Loads);
        Assert.Equal(["http://localhost:9931?model=unsloth%2FQwen3-4B-GGUF%3AQ4_K_M"],
            host.OpenedUris);
    }

    [Fact]
    public async Task Chat_Loads_First_Then_Opens_When_The_Load_Lands()
    {
        var host = new FakeHost();
        var item = InstalledItem();
        var vm = MakeVm(item, host, new FakePreferences());

        await vm.ChatAsync();

        Assert.Same(item, Assert.Single(host.Loads));
        Assert.Empty(host.OpenedUris); // not yet — the load is in flight

        item.IsLoading = true;  // the shell's load path sets this…
        item.IsLoading = false; // …and the poller flips IsLoaded when resident
        item.IsLoaded = true;

        Assert.Single(host.OpenedUris);
    }

    [Fact]
    public async Task Chat_Does_Not_Open_When_The_Load_Is_Rejected()
    {
        var host = new FakeHost();
        var item = InstalledItem();
        var vm = MakeVm(item, host, new FakePreferences());

        await vm.ChatAsync();
        item.LoadFailed = true;

        Assert.Empty(host.OpenedUris);
    }

    [Fact]
    public async Task Chat_Is_A_No_Op_While_The_Model_Is_Busy()
    {
        var host = new FakeHost();
        var item = InstalledItem();
        item.IsDownloading = true;
        var vm = MakeVm(item, host, new FakePreferences());

        await vm.ChatAsync();

        Assert.Empty(host.Loads);
        Assert.Empty(host.OpenedUris);
    }

    [Fact]
    public void CopyModelId_Copies_The_Canonical_Id_And_Raises_The_Check()
    {
        var host = new FakeHost();
        var vm = MakeVm(InstalledItem(), host, new FakePreferences());

        vm.CopyModelId();

        Assert.Equal(["unsloth/Qwen3-4B-GGUF:Q4_K_M"], host.CopiedTexts);
        Assert.True(vm.ModelIdCopied); // resets itself after the feedback delay
    }

    [Fact]
    public void BuildApiRequest_Copies_A_Curl_Command_For_The_Local_Server()
    {
        var host = new FakeHost();
        var vm = MakeVm(InstalledItem(), host, new FakePreferences());

        vm.BuildApiRequest();

        var cmd = Assert.Single(host.CopiedTexts);
        Assert.Contains("http://localhost:9931/v1/chat/completions", cmd);
        Assert.Contains("unsloth/Qwen3-4B-GGUF:Q4_K_M", cmd);
        Assert.StartsWith("curl ", cmd);
        Assert.True(vm.ApiRequestCopied);
    }

    [Fact]
    public void OpenRepository_Uses_The_Repo_Id_Never_The_Display_Name()
    {
        var host = new FakeHost();
        var vm = MakeVm(InstalledItem(), host, new FakePreferences());

        vm.OpenRepository();

        Assert.Equal(["https://huggingface.co/unsloth/Qwen3-4B-GGUF"], host.OpenedUris);
    }

    [Fact]
    public async Task Delete_Forwards_To_The_Host_For_An_Idle_Installed_Model()
    {
        var host = new FakeHost();
        var item = InstalledItem();
        var vm = MakeVm(item, host, new FakePreferences());

        Assert.True(vm.CanDelete);
        await vm.DeleteAsync();

        Assert.Same(item, Assert.Single(host.Deletes));
    }

    [Fact]
    public async Task Delete_Is_Refused_For_A_Loaded_Model()
    {
        var host = new FakeHost();
        var item = InstalledItem();
        item.IsLoaded = true;
        var vm = MakeVm(item, host, new FakePreferences());

        Assert.False(vm.CanDelete);
        await vm.DeleteAsync();

        Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Download_Forwards_For_Hub_Models_Only()
    {
        var host = new FakeHost();
        var hub = HubItem();
        var vm = MakeVm(hub, host, new FakePreferences());

        await vm.DownloadAsync();
        Assert.Same(hub, Assert.Single(host.Downloads));

        // An installed model has no Download action at all.
        var installedVm = MakeVm(InstalledItem(), host, new FakePreferences());
        await installedVm.DownloadAsync();
        Assert.Single(host.Downloads);
    }

    // ---- Live state sync ----

    [Fact]
    public void Action_State_Tracks_The_Shared_ModelItem()
    {
        var item = InstalledItem();
        var vm = MakeVm(item, new FakeHost(), new FakePreferences());
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        item.IsDownloading = true;

        Assert.False(vm.ChatActionEnabled);
        Assert.Equal("Downloading…", vm.ChatActionText);
        Assert.False(vm.CanDelete);
        Assert.Contains(nameof(vm.ChatActionEnabled), raised);
        Assert.Contains(nameof(vm.CanDelete), raised);
    }

    [Fact]
    public void Dispose_Stops_The_State_Sync()
    {
        var item = InstalledItem();
        var vm = MakeVm(item, new FakeHost(), new FakePreferences());
        vm.Dispose();

        item.IsDownloading = true;

        // No crash, no pending chat — the VM is detached.
        Assert.Equal("Downloading…", vm.ChatActionText); // derived on read, still correct
    }

    [Fact]
    public async Task Dispose_Cancels_An_In_Flight_Details_Load()
    {
        var item = InstalledItem();
        var tcs = new TaskCompletionSource<ModelRuntimeDetails?>();
        var vm = new ModelItemDetailsViewModel(item, new FakeHost(),
            _ => null, (_, _) => { },
            runtimeDetailsLoader: async (_, ct) =>
            {
                ct.Register(() => tcs.TrySetCanceled());
                return await tcs.Task;
            });

        var load = vm.InitializeAsync();
        Assert.True(vm.IsLoadingDetails);

        vm.Dispose();
        await load; // must complete, not hang or throw

        Assert.Empty(vm.ContextLengths); // canceled before the ladder was built
    }
}
