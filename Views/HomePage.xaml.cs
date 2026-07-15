using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LlamaApp.Views;

// ModelItem is defined in Views/ModelItem.cs (shared with MainWindow).

/// <summary>Converts a bool to Visibility (true => Visible, false => Collapsed).</summary>
public sealed class BoolToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool b = value is bool v && v;
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        bool visible = invert ? !b : b;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed partial class HomePage : Page
{
    /// <summary>Locally available (downloaded) models — shown with a run/play overlay.</summary>
    public ObservableCollection<ModelItem> DownloadedModels { get; } = new();

    /// <summary>Models available on the Hugging Face Hub for download.</summary>
    public ObservableCollection<ModelItem> HubModels { get; } = new();

    public HomePage()
    {
        InitializeComponent();
        LoadPlaceholders();
    }

    private void LoadPlaceholders()
    {
        // Placeholder downloaded models (would be scanned from ~/.cache/huggingface/hub).
        var logo = new BitmapImage(new Uri("https://huggingface.co/datasets/huggingface/brand-assets/resolve/main/hf-logo.svg"));
        DownloadedModels.Add(new ModelItem
        {
            Name = "Llama 3.2 3B Instruct",
            Description = "Meta Llama 3.2 instruct, GGUF Q4_K_M",
            Parameters = "3.21B",
            Size = "2.0 GB",
            License = "Llama 3.2 Community",
            Logo = logo,
            Downloadable = false
        });
        DownloadedModels.Add(new ModelItem
        {
            Name = "Phi-3.5 Mini Instruct",
            Description = "Microsoft Phi-3.5 mini, GGUF Q4_K_M",
            Parameters = "3.82B",
            Size = "2.4 GB",
            License = "MIT",
            Logo = logo,
            Downloadable = false
        });
        DownloadedModels.Add(new ModelItem
        {
            Name = "Qwen2.5 7B Instruct",
            Description = "Alibaba Qwen2.5 instruct, GGUF Q5_K_M",
            Parameters = "7.62B",
            Size = "5.4 GB",
            License = "Apache 2.0",
            Logo = logo,
            Downloadable = false
        });

        // Placeholder Hugging Face Hub models.
        HubModels.Add(new ModelItem
        {
            Name = "meta-llama/Llama-3.3-70B-Instruct",
            Description = "Meta Llama 3.3 70B Instruct",
            Parameters = "70B",
            Size = "—",
            License = "Llama 3.3 Community",
            Logo = logo,
            Downloadable = true
        });
        HubModels.Add(new ModelItem
        {
            Name = "deepseek-ai/DeepSeek-R1",
            Description = "DeepSeek R1 reasoning model",
            Parameters = "671B",
            Size = "—",
            License = "MIT",
            Logo = logo,
            Downloadable = true
        });
        HubModels.Add(new ModelItem
        {
            Name = "Qwen/Qwen2.5-Coder-32B-Instruct",
            Description = "Qwen2.5 Coder instruct-tuned",
            Parameters = "32B",
            Size = "—",
            License = "Apache 2.0",
            Logo = logo,
            Downloadable = true
        });
        HubModels.Add(new ModelItem
        {
            Name = "microsoft/Phi-4",
            Description = "Microsoft Phi-4",
            Parameters = "14B",
            Size = "—",
            License = "MIT",
            Logo = logo,
            Downloadable = true
        });
    }

    /// <summary>Show the hover overlay (play/download icon) when the pointer enters a card.</summary>
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.FindName("HoverOverlay") is Border overlay)
            overlay.Opacity = 1;
    }

    /// <summary>Hide the hover overlay when the pointer leaves a card.</summary>
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.FindName("HoverOverlay") is Border overlay)
            overlay.Opacity = 0;
    }

    /// <summary>
    /// Adaptively sizes the GridView's items so each row is fully filled with
    /// equal-sized square cards. Columns are chosen from a desired minimum card
    /// width, then the available width is divided evenly across them.
    /// </summary>
    private void ModelGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is GridView gv && gv.ItemsPanelRoot is Microsoft.UI.Xaml.Controls.ItemsWrapGrid panel)
        {
            const double minItemWidth = 170;
            double available = e.NewSize.Width;
            if (available <= 0) return;

            int columns = Math.Max(1, (int)Math.Floor(available / minItemWidth));
            double slot = available / columns;

            panel.ItemWidth = slot;
            panel.ItemHeight = slot; // keep cards square
        }
    }
}
