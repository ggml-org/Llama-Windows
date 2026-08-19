using Microsoft.UI.Xaml;

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
        Bindings.Update();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ContextOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ContextLengthOption option })
            ViewModel?.SelectContextLength(option);
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
