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
        public string ServerAddress => "localhost";
        public string? ApiKey => null;
        public List<string> CopiedTexts { get; } = [];
        public List<string> OpenedUris { get; } = [];
        public List<ModelItem> Loads { get; } = [];
        public List<ModelItem> Downloads { get; } = [];
        public List<ModelItem> Deletes { get; } = [];
        public int CloseDetailsCalls;

        /// <summary>The raw ctx-size currently "in models.user.ini", if any.</summary>
        public string? CtxSizeOverride;

        /// <summary>Every server model id the ViewModel looked an override up with.</summary>
        public List<string> OverrideLookups { get; } = [];

        /// <summary>Every server model id the ViewModel asked to edit, as passed.</summary>
        public List<string> EditPresetCalls { get; } = [];

        /// <summary>Hook to gate or fail the edit flow (null = complete at once).</summary>
        public Func<string, Task>? OnEditPreset;

        public Task LoadModelAsync(ModelItem model) { Loads.Add(model); return Task.CompletedTask; }
        public Task DownloadAsync(ModelItem model) { Downloads.Add(model); return Task.CompletedTask; }
        public Task<bool> DeleteAsync(ModelItem model) { Deletes.Add(model); return Task.FromResult(true); }
        public void CopyText(string text) => CopiedTexts.Add(text);
        public void OpenUri(string uri) => OpenedUris.Add(uri);
        public void CloseDetails() => CloseDetailsCalls++;

        public Task EditModelPresetAsync(string serverModelId)
        {
            EditPresetCalls.Add(serverModelId);
            return OnEditPreset?.Invoke(serverModelId) ?? Task.CompletedTask;
        }

        public string? GetContextSizeOverride(string serverModelId)
        {
            OverrideLookups.Add(serverModelId);
            return CtxSizeOverride;
        }
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
        Func<string, int, CancellationToken, Task<HardwareFitLevel?>>? fitParamsProbe = null,
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
        // Nothing is preselected until the user picks — an unset context
        // must not read as an applied 4k (and never silently the largest).
        Assert.Null(vm.SelectedContextLength);
        Assert.All(vm.ContextLengths, o => Assert.False(o.IsSelected));
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
        // No preselection → the header stays empty until the user picks.
        Assert.True(string.IsNullOrEmpty(vm.EstimatedMemoryDisplay));
        vm.SelectContextLength(vm.ContextLengths[0]); // 4k
        Assert.False(string.IsNullOrEmpty(vm.EstimatedMemoryDisplay));
    }

    [Fact]
    public async Task InitializeAsync_Without_Metadata_Tints_Every_Option_Might()
    {
        // Nothing is known about the size — "might fit" is the honest
        // verdict, and every option stays selectable (fail open).
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(), details: null);
        await vm.InitializeAsync();

        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSupported));
        Assert.All(vm.ContextLengths, o => Assert.Equal(HardwareFitLevel.Might, o.FitLevel));
        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSelectable));
        Assert.All(vm.ContextLengths, o => Assert.Equal("Memory requirement unknown — might fit", o.TooltipText));
        Assert.Null(vm.SelectedContextLength);
    }

    [Fact]
    public async Task Context_Cells_Tint_Green_Yellow_Red_Against_The_Memory_Budget()
    {
        // A 256k-max model so the context guard stays out of the way: only
        // the memory bands decide. Estimates: 4 GB weights + 128 KiB of KV
        // per token — 32k needs ~8.3 GB, 64k ~12.6 GB. Budget 9 GB →
        // fit ≤ 8.1 GB, might ≤ 11.25 GB, no way beyond.
        var details256K = new ModelRuntimeDetails(
            ModelSizeBytes: 4_000_000_000L,
            ContextInfo: new GgufContextInfo("llama", 262144, 32, 4096, 32, 8, 128));
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details256K, memoryBudget: () => 9_000_000_000UL);
        await vm.InitializeAsync();

        Assert.Equal(
            [HardwareFitLevel.Fit, HardwareFitLevel.Fit, HardwareFitLevel.Fit,
             HardwareFitLevel.Might, HardwareFitLevel.NoWay,
             HardwareFitLevel.NoWay, HardwareFitLevel.NoWay],
            vm.ContextLengths.Select(o => o.FitLevel).ToArray());
        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSupported));

        // Yellow stays selectable ("might" means try it); only red disables.
        Assert.True(vm.ContextLengths[3].IsSelectable);
        Assert.False(vm.ContextLengths[4].IsSelectable);
        Assert.False(vm.ContextLengths[5].IsSelectable);

        // Green, yellow and red each explain themselves on hover.
        var green = vm.ContextLengths[0].TooltipText; // 4k
        Assert.StartsWith("Requires ", green);
        Assert.EndsWith(" of memory", green);

        var yellow = vm.ContextLengths[3].TooltipText; // 32k
        Assert.StartsWith("Might not fit: requires ", yellow);
        Assert.Contains("GB", yellow);

        var red = vm.ContextLengths[4].TooltipText; // 64k
        Assert.StartsWith("Model requires at least ", red);
        Assert.EndsWith(" of memory", red);
        Assert.Contains("GB", red);

        // The accessible name carries the level as text.
        Assert.Equal("32k, might fit", vm.ContextLengths[3].AccessibleName);

        // A red option can't be selected; an yellow one can.
        vm.SelectContextLength(vm.ContextLengths[4]);
        Assert.Null(vm.SelectedContextLength);
        vm.SelectContextLength(vm.ContextLengths[3]);
        Assert.Equal(32768, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task Downloadable_Models_Tint_The_Ladder_From_The_Catalog_Size()
    {
        // A Hub row with a known size gets the ladder (no GGUF header yet —
        // every option shows the bare weights) and is tinted out of the box
        // from whether the weights fit: the budget is VRAM + usable RAM, so
        // a 20 GB model is a fit on a 16 GB GPU + 8 GB usable RAM machine…
        var hub = HubItem();
        hub.SizeBytes = 20_000_000_000UL;
        var vm = MakeVm(hub, new FakeHost(), new FakePreferences(),
            details: null, memoryBudget: () => 24_000_000_000UL);
        await vm.InitializeAsync();

        Assert.True(vm.ContextSectionVisible);
        Assert.Equal(7, vm.ContextLengths.Count);
        Assert.All(vm.ContextLengths, o => Assert.True(o.IsSupported));
        Assert.All(vm.ContextLengths, o => Assert.Equal(HardwareFitLevel.Fit, o.FitLevel));
        Assert.All(vm.ContextLengths, o => Assert.Equal(20_000_000_000L, o.EstimatedMemoryBytes));
    }

    [Fact]
    public async Task Downloadable_Models_Whose_Weights_Fit_Nowhere_Turn_Every_Option_Red()
    {
        // …but red across the board when the weights alone blow past the
        // optimism band — "can this model even load here", answered before
        // the download.
        var hub = HubItem();
        hub.SizeBytes = 40_000_000_000UL;
        var vm = MakeVm(hub, new FakeHost(), new FakePreferences(),
            details: null, memoryBudget: () => 24_000_000_000UL);
        await vm.InitializeAsync();

        Assert.True(vm.ContextSectionVisible);
        Assert.All(vm.ContextLengths, o => Assert.Equal(HardwareFitLevel.NoWay, o.FitLevel));
        Assert.All(vm.ContextLengths, o => Assert.False(o.IsSelectable));
        Assert.All(vm.ContextLengths, o => Assert.StartsWith("Model requires at least ", o.TooltipText));
    }

    [Fact]
    public async Task InitializeAsync_Refines_Option_Fit_With_FitParams_Verdicts()
    {
        // Heuristic budget is unbounded (everything is green), then
        // fit-params says only 16k and below actually fit — the 32k option
        // must flip to red. Unsupported options (64k+, above the 32k max)
        // are never probed.
        var probed = new List<int>();
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details: Details32K with { FilePath = "/fake/model.gguf" },
            fitParamsProbe: (_, tokens, _) =>
            {
                probed.Add(tokens);
                return Task.FromResult<HardwareFitLevel?>(
                    tokens <= 16384 ? HardwareFitLevel.Fit : HardwareFitLevel.NoWay);
            });
        await vm.InitializeAsync();

        Assert.Equal([4096, 8192, 16384, 32768], probed);
        Assert.Equal(
            [HardwareFitLevel.Fit, HardwareFitLevel.Fit, HardwareFitLevel.Fit,
             HardwareFitLevel.NoWay, HardwareFitLevel.Fit,
             HardwareFitLevel.Fit, HardwareFitLevel.Fit],
            vm.ContextLengths.Select(o => o.FitLevel).ToArray());
        Assert.False(vm.ContextLengths[3].IsSelectable);
    }

    [Fact]
    public async Task InitializeAsync_Keeps_Heuristic_Tints_When_FitParams_Has_No_Verdict()
    {
        // A null verdict (no binary, CPU-era build, unreadable model) keeps
        // the heuristic: fit-params never overrides with "unknown".
        var vm = MakeVm(InstalledItem(), new FakeHost(), new FakePreferences(),
            details: Details32K with { FilePath = "/fake/model.gguf" },
            memoryBudget: () => 5_000_000_000UL,
            fitParamsProbe: (_, _, _) => Task.FromResult<HardwareFitLevel?>(null));
        await vm.InitializeAsync();

        // 4 GB weights + 128 KiB KV/token against a 5 GB budget: 4k (~4.5 GB)
        // is a tight fit (yellow), 8k and 16k yellow too, 32k (~8.3 GB) red —
        // exactly what the heuristic said before the probe ran.
        Assert.Equal(
            [HardwareFitLevel.Might, HardwareFitLevel.Might, HardwareFitLevel.Might,
             HardwareFitLevel.NoWay, HardwareFitLevel.NoWay,
             HardwareFitLevel.NoWay, HardwareFitLevel.NoWay],
            vm.ContextLengths.Select(o => o.FitLevel).ToArray());
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
            fitParamsProbe: (_, tokens, _) => Task.FromResult<HardwareFitLevel?>(
                tokens <= 8192 ? HardwareFitLevel.Fit : HardwareFitLevel.NoWay),
            dispatchToUi: dispatched.Add);
        await vm.InitializeAsync();

        var option = vm.ContextLengths[2]; // 16k — green from the heuristic, red from fit-params
        Assert.Equal(HardwareFitLevel.Fit, option.FitLevel); // queued, not applied yet
        Assert.NotEmpty(dispatched);

        var raised = new List<string?>();
        option.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        dispatched.ForEach(a => a());

        Assert.Equal(HardwareFitLevel.NoWay, option.FitLevel);
        Assert.False(option.IsSelectable);
        Assert.Contains("FitLevel", raised);
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
        Assert.Equal(HardwareFitLevel.Fit, capped.FitLevel); // memory is fine — the cap still wins
        Assert.False(capped.IsSelectable);
        Assert.Equal("This model supports up to 32k of context", capped.TooltipText);
    }

    [Fact]
    public async Task RestoreSelection_Skips_A_Memory_Capped_Preference()
    {
        // ~5 GB free: 32k (~8.3 GB) is a hard no — the persisted 32k
        // preference falls away to NOTHING preselected (an over-budget saved
        // value must not silently become 4k).
        var prefs = new FakePreferences();
        prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"] = 32768;
        var vm = MakeVm(InstalledItem(), new FakeHost(), prefs,
            memoryBudget: () => 5_000_000_000UL);
        await vm.InitializeAsync();

        Assert.Null(vm.SelectedContextLength);
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

        Assert.Null(vm.SelectedContextLength);
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

        Assert.Null(vm.SelectedContextLength);
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

        Assert.Null(vm2.SelectedContextLength);
    }

    // ---- models.user.ini overrides ----

    [Fact]
    public async Task Override_Lookups_And_Edit_Use_The_Case_Preserved_Repo_Quant_Id()
    {
        // The user file keys sections by the exact [repo:QUANT] header — a
        // re-cased id would silently miss every pinned value and edit the
        // wrong section.
        var host = new FakeHost();
        var item = new ModelItem
        {
            Name = "DeepSeek-V4-Flash",
            RepoName = "unsloth/DeepSeek-V4-Flash-0731-GGUF",
            Quant = "UD-Q2_K_XL",
        };
        var vm = MakeVm(item, host, new FakePreferences());

        await vm.InitializeAsync();
        await vm.EditModelPresetAsync();

        const string id = "unsloth/DeepSeek-V4-Flash-0731-GGUF:UD-Q2_K_XL";
        Assert.NotEmpty(host.OverrideLookups);
        Assert.All(host.OverrideLookups, lookedUp => Assert.Equal(id, lookedUp));
        Assert.Equal([id], host.EditPresetCalls);
    }

    [Fact]
    public async Task Edit_Preset_Is_Offered_For_Installed_Models_Only()
    {
        var installedHost = new FakeHost();
        var installed = MakeVm(InstalledItem(), installedHost, new FakePreferences());
        Assert.True(installed.EditPresetVisible);
        Assert.True(installed.EditPresetEnabled);

        var hubHost = new FakeHost();
        var hub = MakeVm(HubItem(), hubHost, new FakePreferences());
        Assert.False(hub.EditPresetVisible);
        Assert.False(hub.EditPresetEnabled);

        await hub.EditModelPresetAsync(); // a Hub row has nothing to configure
        Assert.Empty(hubHost.EditPresetCalls);
    }

    [Fact]
    public async Task Edit_Preset_Is_Forwarded_While_The_Model_Is_Loaded()
    {
        // A resident model is exactly when a user tunes sampler settings —
        // the edit must not be gated on the model being idle.
        var host = new FakeHost();
        var item = InstalledItem();
        item.IsLoaded = true;
        var vm = MakeVm(item, host, new FakePreferences());

        await vm.EditModelPresetAsync();

        Assert.Equal(["unsloth/Qwen3-4B-GGUF:Q4_K_M"], host.EditPresetCalls);
    }

    [Fact]
    public async Task Edit_Preset_Is_Single_Flight()
    {
        var host = new FakeHost();
        var gate = new TaskCompletionSource();
        host.OnEditPreset = _ => gate.Task;
        var vm = MakeVm(InstalledItem(), host, new FakePreferences());

        var first = vm.EditModelPresetAsync();
        Assert.True(vm.IsEditPresetInFlight);
        Assert.False(vm.EditPresetEnabled);

        await vm.EditModelPresetAsync(); // a second click while in flight — ignored
        Assert.False(vm.EditPresetEnabled);

        gate.SetResult();
        await first;

        Assert.False(vm.IsEditPresetInFlight);
        Assert.True(vm.EditPresetEnabled);
        Assert.Equal(["unsloth/Qwen3-4B-GGUF:Q4_K_M"], host.EditPresetCalls);
    }

    [Fact]
    public async Task Edit_Preset_Failure_Lands_In_The_Error_Line_And_Restores_Availability()
    {
        var host = new FakeHost();
        host.OnEditPreset = _ => throw new IOException("access denied");
        var vm = MakeVm(InstalledItem(), host, new FakePreferences());

        await vm.EditModelPresetAsync();

        Assert.True(vm.HasError);
        Assert.Contains("models.user.ini", vm.ErrorMessage);
        Assert.False(vm.IsEditPresetInFlight);
        Assert.True(vm.EditPresetEnabled);

        // The error clears on the next details load.
        host.OnEditPreset = null;
        await vm.InitializeAsync();
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_Pinned_Ctx_Size_Replaces_The_Picker_And_Blocks_Selection()
    {
        var prefs = new FakePreferences();
        prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"] = 16384;
        var host = new FakeHost { CtxSizeOverride = "24576" }; // not a ladder value
        var vm = MakeVm(InstalledItem(), host, prefs);
        await vm.InitializeAsync();

        Assert.True(vm.HasContextSizeOverride);
        Assert.Equal("24576", vm.ContextSizeOverride);
        Assert.Equal("ctx-size = 24576 — set in models.user.ini", vm.ContextSizeOverrideDisplay);
        Assert.False(vm.ContextPickerVisible);

        // The saved 16k is NOT restored — the picker must not read as an
        // applied context while the user's value is in charge.
        Assert.Null(vm.SelectedContextLength);
        Assert.True(string.IsNullOrEmpty(vm.EstimatedMemoryDisplay));

        // Selecting is blocked outright: no selection, no persistence.
        vm.SelectContextLength(vm.ContextLengths[2]);
        Assert.Null(vm.SelectedContextLength);
        Assert.Equal(16384, prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"]);
    }

    [Fact]
    public async Task Arbitrary_Override_Values_Are_Shown_Verbatim()
    {
        // Whatever the user typed — off-ladder, padded, non-numeric — is
        // presented exactly as written; nothing is parsed or rounded.
        foreach (var raw in new[] { "12288", "32768", "  8192  ", "banana", "0" })
        {
            var host = new FakeHost { CtxSizeOverride = raw };
            var vm = MakeVm(InstalledItem(), host, new FakePreferences());
            await vm.InitializeAsync();

            Assert.Equal(raw, vm.ContextSizeOverride);
            Assert.Equal($"ctx-size = {raw} — set in models.user.ini", vm.ContextSizeOverrideDisplay);
        }
    }

    [Fact]
    public async Task RefreshOverridesAsync_Picks_Up_An_Override_Added_And_Removed()
    {
        var prefs = new FakePreferences();
        prefs.Saved["unsloth/Qwen3-4B-GGUF:Q4_K_M"] = 16384;
        var host = new FakeHost();
        var vm = MakeVm(InstalledItem(), host, prefs);
        await vm.InitializeAsync();

        Assert.False(vm.HasContextSizeOverride);
        Assert.Equal(16384, vm.SelectedContextLength?.Tokens);
        Assert.False(string.IsNullOrEmpty(vm.EstimatedMemoryDisplay));

        // Added while the page is open: the picker (and its estimate) swaps
        // out for the pinned value.
        host.CtxSizeOverride = "32768";
        await vm.RefreshOverridesAsync();

        Assert.Equal("32768", vm.ContextSizeOverride);
        Assert.False(vm.ContextPickerVisible);
        Assert.Null(vm.SelectedContextLength);
        Assert.True(string.IsNullOrEmpty(vm.EstimatedMemoryDisplay));

        // Removed: the picker and the persisted selection come back.
        host.CtxSizeOverride = null;
        await vm.RefreshOverridesAsync();

        Assert.False(vm.HasContextSizeOverride);
        Assert.True(vm.ContextPickerVisible);
        Assert.Equal(16384, vm.SelectedContextLength?.Tokens);
    }

    [Fact]
    public async Task Pinned_Ctx_Size_Text_Notifies_The_Binding_On_Every_Change()
    {
        // The pinned line's TextBlock binds ContextSizeOverrideDisplay (OneWay
        // x:Bind): the computed text re-renders only when the setter raises
        // PropertyChanged for that name. Getter assertions can't see this —
        // the binding goes stale without the notification, leaving the text
        // blank on first population (ShowDetails binds before InitializeAsync
        // fills the cache) and stale on every later change.
        var host = new FakeHost { CtxSizeOverride = "24576" };
        var vm = MakeVm(InstalledItem(), host, new FakePreferences());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // First population — the value lands after the binding is attached.
        await vm.InitializeAsync();
        Assert.Contains(nameof(vm.ContextSizeOverrideDisplay), raised);
        Assert.Equal("ctx-size = 24576 — set in models.user.ini", vm.ContextSizeOverrideDisplay);

        // A different value while the page is open.
        raised.Clear();
        host.CtxSizeOverride = "32768";
        await vm.RefreshOverridesAsync();
        Assert.Contains(nameof(vm.ContextSizeOverrideDisplay), raised);
        Assert.Equal("ctx-size = 32768 — set in models.user.ini", vm.ContextSizeOverrideDisplay);

        // And removal: the text clears, and that is a binding change too.
        raised.Clear();
        host.CtxSizeOverride = null;
        await vm.RefreshOverridesAsync();
        Assert.Contains(nameof(vm.ContextSizeOverrideDisplay), raised);
        Assert.Equal("", vm.ContextSizeOverrideDisplay);
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
