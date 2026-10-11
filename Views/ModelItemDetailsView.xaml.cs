using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using LlamaApp.Llama;

namespace LlamaApp.Views;

/// <summary>
/// The model details panel. Presentation only: every action forwards to
/// <see cref="ModelItemDetailsViewModel"/>, which owns the interaction state
/// and orchestrates the shell's workflows. Hosted inside the main flyout's
/// models card (MainWindow swaps it with the list) — no window or frame
/// navigation of its own.
/// </summary>
public sealed partial class ModelItemDetailsView : UserControl
{
    /// <summary>Raised by the back row; the shell swaps the list back in.</summary>
    public event EventHandler? BackRequested;

    /// <summary>The details ViewModel currently bound; null until the first show.</summary>
    public ModelItemDetailsViewModel? ViewModel { get; private set; }

    public ModelItemDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Binds a new ViewModel and refreshes the compiled bindings (the view
    /// instance is reused across shows; the ViewModel is created per show).
    /// </summary>
    public void SetViewModel(ModelItemDetailsViewModel viewModel)
    {
        ViewModel = viewModel;
        ModelGpuDeviceBox.Items.Clear();
        ModelGpuDeviceBox.Items.Add(new Microsoft.UI.Xaml.Controls.ComboBoxItem
            { Content = "Use server setting", Tag = "" });
        ModelGpuDeviceBox.Items.Add(new Microsoft.UI.Xaml.Controls.ComboBoxItem
            { Content = "CPU", Tag = "none" });
        if (viewModel.PromptProfile.GpuDeviceName is { Length: > 0 } saved && saved != "none")
            ModelGpuDeviceBox.Items.Add(new Microsoft.UI.Xaml.Controls.ComboBoxItem
                { Content = saved + " (checking availability)", Tag = saved });
        PopulateProfileControls(viewModel.PromptProfile);
        PromptProfileStatus.Text = "";
        ModelGpuMemoryStatus.Text = "Checking GPU memory…";
        Bindings.Update();
        _ = PopulateGpuDevicesAsync(viewModel);
        _ = PopulateCapabilitiesAsync(viewModel);
    }

    private async Task PopulateCapabilitiesAsync(ModelItemDetailsViewModel viewModel)
    {
        if (LlamaManager.Shared.BinaryPath is not { } binary)
        {
            ModelCapabilityStatus.Text = "Options are checked after llama.cpp is installed.";
            return;
        }
        var caps = await ServeCapabilities.ProbeAsync(binary);
        if (ViewModel != viewModel) return;
        if (!caps.Succeeded)
        {
            ModelCapabilityStatus.Text = "Could not read the installed llama.cpp options.";
            return;
        }
        ModelGpuDeviceBox.IsEnabled = caps.Supports("--device");
        ModelGpuLayersBox.IsEnabled = caps.Supports("--n-gpu-layers");
        ModelGpuLayerCountBox.IsEnabled = caps.Supports("--n-gpu-layers");
        ModelBatchSizeBox.IsEnabled = caps.Supports("--batch-size");
        ModelMicroBatchSizeBox.IsEnabled = caps.Supports("--ubatch-size");
        ModelFlashAttentionBox.IsEnabled = caps.Supports("--flash-attn");
        ModelCacheKBox.IsEnabled = caps.Supports("--cache-type-k");
        ModelCacheVBox.IsEnabled = caps.Supports("--cache-type-v");
        ModelThreadsBox.IsEnabled = caps.Supports("--threads");
        ModelThreadsBatchBox.IsEnabled = caps.Supports("--threads-batch");
        ModelParallelBox.IsEnabled = caps.Supports("--parallel");
        ModelTemperatureBox.IsEnabled = caps.Supports("--temp");
        ModelTopKBox.IsEnabled = caps.Supports("--top-k");
        ModelTopPBox.IsEnabled = caps.Supports("--top-p");
        ModelRepeatPenaltyBox.IsEnabled = caps.Supports("--repeat-penalty");
        ModelSplitModeBox.IsEnabled = caps.Supports("--split-mode");
        ModelTensorSplitBox.IsEnabled = caps.Supports("--tensor-split");
        ModelCapabilityStatus.Text = "Unavailable controls are disabled for the installed build.";
    }

    private async Task PopulateGpuDevicesAsync(ModelItemDetailsViewModel viewModel)
    {
        try
        {
            var probe = await LlamaManager.Shared.ProbeDevicesAsync();
            if (ViewModel != viewModel) return;
            var devices = probe.Devices.Where(d =>
                d.Kind is not (DeviceKind.Cpu or DeviceKind.Unknown)).ToArray();
            ModelGpuMemoryStatus.Text = !probe.Succeeded
                ? "Could not read GPU memory from the installed runtime."
                : devices.Length == 0
                    ? "No GPUs were reported by the installed runtime."
                    : "Free memory is a snapshot from the installed runtime and can change.";
            var saved = viewModel.PromptProfile.GpuDeviceName;
            foreach (var device in devices)
            {
                var key = GpuDeviceChoice.Key(device, devices);
                var existing = ModelGpuDeviceBox.Items.OfType<Microsoft.UI.Xaml.Controls.ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag as string == key ||
                        item.Tag as string == saved && GpuDeviceChoice.Resolve(saved, devices) == device);
                if (existing is not null)
                {
                    existing.Content = GpuDeviceChoice.Label(device);
                    existing.Tag = key;
                }
                else ModelGpuDeviceBox.Items.Add(new Microsoft.UI.Xaml.Controls.ComboBoxItem
                    { Content = GpuDeviceChoice.Label(device), Tag = key });
            }
            var savedItem = ModelGpuDeviceBox.Items.OfType<Microsoft.UI.Xaml.Controls.ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == saved);
            if (savedItem is not null && (savedItem.Content as string)?.EndsWith("(checking availability)") == true)
                savedItem.Content = $"{saved} (unavailable or ambiguous; choose a GPU again)";
        }
        catch (Exception ex)
        {
            LlamaApp.Common.Log.Warn(ex, "model GPU probe failed");
            if (ViewModel == viewModel)
                ModelGpuMemoryStatus.Text = "Could not read GPU memory from the installed runtime.";
        }
    }

    private static void SelectTag(Microsoft.UI.Xaml.Controls.ComboBox box, string? tag)
    {
        box.SelectedItem = box.Items.OfType<Microsoft.UI.Xaml.Controls.ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == tag) ?? box.Items[0];
    }

    private void PopulateProfileControls(ModelPromptProcessingProfile profile)
    {
        SelectTag(ModelGpuDeviceBox, profile.GpuDeviceName);
        SelectTag(ModelGpuLayersBox, int.TryParse(profile.GpuLayers, out _) ? "custom" : profile.GpuLayers);
        ModelGpuLayerCountBox.Value = int.TryParse(profile.GpuLayers, out var layers) ? layers : double.NaN;
        ModelBatchSizeBox.Value = profile.BatchSize;
        ModelMicroBatchSizeBox.Value = profile.MicroBatchSize;
        SelectTag(ModelFlashAttentionBox, profile.FlashAttention);
        SelectTag(ModelCacheKBox, profile.CacheTypeK);
        SelectTag(ModelCacheVBox, profile.CacheTypeV);
        ModelThreadsBox.Value = profile.Threads;
        ModelThreadsBatchBox.Value = profile.ThreadsBatch;
        ModelParallelBox.Value = profile.Parallel;
        ModelTemperatureBox.Value = profile.Temperature ?? double.NaN;
        ModelTopKBox.Value = profile.TopK ?? double.NaN;
        ModelTopPBox.Value = profile.TopP ?? double.NaN;
        ModelRepeatPenaltyBox.Value = profile.RepeatPenalty ?? double.NaN;
        SelectTag(ModelSplitModeBox, profile.SplitMode);
        ModelTensorSplitBox.Text = profile.TensorSplit ?? "";
    }

    /// <summary>Moves keyboard focus into the view when it opens (best effort).</summary>
    public void FocusFirst() => BackButton.Focus(FocusState.Programmatic);

    private void Back_Click(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ContextOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ContextLengthOption option })
            ViewModel?.SelectContextLength(option);
    }

    private void SavePromptProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var batch = double.IsNaN(ModelBatchSizeBox.Value) ? 0
            : (int)Math.Clamp(ModelBatchSizeBox.Value, 0, InferenceTuning.MaxBatchSize);
        var microbatch = double.IsNaN(ModelMicroBatchSizeBox.Value) ? 0
            : (int)Math.Clamp(ModelMicroBatchSizeBox.Value, 0, InferenceTuning.MaxBatchSize);
        var flash = (ModelFlashAttentionBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string
            ?? "auto";
        var layersChoice = (ModelGpuLayersBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string ?? "";
        if (layersChoice == "custom" && double.IsNaN(ModelGpuLayerCountBox.Value))
        {
            PromptProfileStatus.Text = "Enter a GPU layer count.";
            return;
        }
        var otherOptions = new ModelPromptProcessingProfile
        {
            GpuDeviceName = (ModelGpuDeviceBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string ?? "",
            GpuLayers = layersChoice == "custom" ? ((int)ModelGpuLayerCountBox.Value).ToString() : layersChoice,
            CacheTypeK = (ModelCacheKBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string ?? "",
            CacheTypeV = (ModelCacheVBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string ?? "",
            Threads = double.IsNaN(ModelThreadsBox.Value) ? 0 : (int)ModelThreadsBox.Value,
            ThreadsBatch = double.IsNaN(ModelThreadsBatchBox.Value) ? 0 : (int)ModelThreadsBatchBox.Value,
            Parallel = double.IsNaN(ModelParallelBox.Value) ? 0 : (int)ModelParallelBox.Value,
            Temperature = double.IsNaN(ModelTemperatureBox.Value) ? null : ModelTemperatureBox.Value,
            TopK = double.IsNaN(ModelTopKBox.Value) ? null : (int)ModelTopKBox.Value,
            TopP = double.IsNaN(ModelTopPBox.Value) ? null : ModelTopPBox.Value,
            RepeatPenalty = double.IsNaN(ModelRepeatPenaltyBox.Value) ? null : ModelRepeatPenaltyBox.Value,
            SplitMode = (ModelSplitModeBox.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string ?? "",
            TensorSplit = ModelTensorSplitBox.Text.Trim(),
        };
        var kvChanged = vm.PromptProfile.CacheTypeK != otherOptions.CacheTypeK ||
            vm.PromptProfile.CacheTypeV != otherOptions.CacheTypeV;
        var error = vm.SavePromptProfile(batch, microbatch, flash, otherOptions);
        PromptProfileStatus.Text = error ?? (kvChanged
            ? "Saved for the next model load. Reopen details to refresh memory estimates."
            : "Saved for the next model load. Reload a loaded model to apply it.");
    }

    private void ResetPromptProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.ResetPromptProfile();
        PopulateProfileControls(vm.PromptProfile);
        PromptProfileStatus.Text = "Server settings restored for the next model load.";
    }

    private async void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.ChatAsync();
    }

    private void CopyModelId_Click(object sender, RoutedEventArgs e)
        => ViewModel?.CopyModelId();

    private void BuildApiRequest_Click(object sender, RoutedEventArgs e)
        => ViewModel?.BuildApiRequest();

    private void OpenRepository_Click(object sender, RoutedEventArgs e)
        => ViewModel?.OpenRepository();

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.DownloadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.DeleteAsync();
    }
}
