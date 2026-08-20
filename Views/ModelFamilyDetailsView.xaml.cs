using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace LlamaApp.Views
{
    /// <summary>
    /// The model family details panel. Presentation only: every action
    /// forwards to <see cref="ModelFamilyDetailsViewModel"/>, which owns the
    /// size/variant selection and orchestrates the shell's download workflow.
    /// Hosted inside the main flyout's models card (MainWindow swaps it with
    /// the list) — no window or frame navigation of its own. Mirrors
    /// <see cref="ModelItemDetailsView"/>.
    /// </summary>
    public sealed partial class ModelFamilyDetailsView : UserControl
    {
        /// <summary>Raised by the back row; the shell swaps the list back in.</summary>
        public event System.EventHandler? BackRequested;

        /// <summary>The details ViewModel currently bound; null until the first show.</summary>
        public ModelFamilyDetailsViewModel? ViewModel { get; private set; }

        public ModelFamilyDetailsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds a new ViewModel and refreshes the compiled bindings (the
        /// view instance is reused across shows; the ViewModel is created
        /// per show).
        /// </summary>
        public void SetViewModel(ModelFamilyDetailsViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        /// <summary>Moves keyboard focus into the view when it opens (best effort).</summary>
        public void FocusFirst() => BackButton.Focus(FocusState.Programmatic);

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, System.EventArgs.Empty);

        private void SizeOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ModelFamilySizeOption option })
                ViewModel?.SelectSize(option);
        }

        private void Variant_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ModelFamilyVariantOption option })
                ViewModel?.DownloadVariant(option);
        }
    }
}
