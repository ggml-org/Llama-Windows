using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LlamaApp.Views;

/// <summary>
/// Lightweight view-model item used by the HomePage placeholders.
/// Represents either a locally downloaded GGUF model or a Hugging Face Hub model.
/// </summary>
public sealed class ModelItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Parameters { get; set; } = "";
    public string Size { get; set; } = "";
    public string License { get; set; } = "";
    public ImageSource? Logo { get; set; }
}

public sealed partial class HomePage : Page
{
    /// <summary>Downloaded GGUF models (carousel).</summary>
    public ObservableCollection<ModelItem> DownloadedModels { get; } = new();

    /// <summary>Models available on the Hugging Face Hub (download list).</summary>
    public ObservableCollection<ModelItem> HubModels { get; } = new();

    public HomePage()
    {
        InitializeComponent();
        LoadPlaceholders();
    }

    private void LoadPlaceholders()
    {
        // Placeholder downloaded models (would be scanned from ~/.cache/huggingface/hub).
        DownloadedModels.Add(new ModelItem
        {
            Name = "Llama 3.2 3B Instruct",
            Description = "Meta Llama 3.2 instruct-tuned, GGUF Q4_K_M",
            Parameters = "3.21B",
            Size = "2.0 GB",
            License = "Llama 3.2 Community",
            Logo = new BitmapImage(new Uri("https://huggingface.co/datasets/huggingface/brand-assets/resolve/main/hf-logo.svg"))
        });
        DownloadedModels.Add(new ModelItem
        {
            Name = "Phi-3.5 Mini Instruct",
            Description = "Microsoft Phi-3.5 mini, GGUF Q4_K_M",
            Parameters = "3.82B",
            Size = "2.4 GB",
            License = "MIT",
            Logo = new BitmapImage(new Uri("https://huggingface.co/datasets/huggingface/brand-assets/resolve/main/hf-logo.svg"))
        });
        DownloadedModels.Add(new ModelItem
        {
            Name = "Qwen2.5 7B Instruct",
            Description = "Alibaba Qwen2.5 instruct, GGUF Q5_K_M",
            Parameters = "7.62B",
            Size = "5.4 GB",
            License = "Apache 2.0",
            Logo = new BitmapImage(new Uri("https://huggingface.co/datasets/huggingface/brand-assets/resolve/main/hf-logo.svg"))
        });

        // Placeholder Hugging Face Hub models.
        HubModels.Add(new ModelItem
        {
            Name = "meta-llama/Llama-3.3-70B-Instruct",
            Description = "Meta Llama 3.3 70B Instruct",
            Parameters = "70B",
            Size = "—",
            License = "Llama 3.3 Community"
        });
        HubModels.Add(new ModelItem
        {
            Name = "deepseek-ai/DeepSeek-R1",
            Description = "DeepSeek R1 reasoning model",
            Parameters = "671B",
            Size = "—",
            License = "MIT"
        });
        HubModels.Add(new ModelItem
        {
            Name = "Qwen/Qwen2.5-Coder-32B-Instruct",
            Description = "Qwen2.5 Coder instruct-tuned",
            Parameters = "32B",
            Size = "—",
            License = "Apache 2.0"
        });
        HubModels.Add(new ModelItem
        {
            Name = "microsoft/Phi-4",
            Description = "Microsoft Phi-4",
            Parameters = "14B",
            Size = "—",
            License = "MIT"
        });
    }
}
