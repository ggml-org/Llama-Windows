using Microsoft.UI.Xaml.Navigation;

namespace LlamaApp.Views
{
    
    
    /// <summary>
    /// A simple page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            MenuControl.SelectedItem = MenuControl.MenuItems.OfType<NavigationViewItem>().First();
            ContentFrame.Navigate(typeof(HomePage), null,
                new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }

        private void ContentFrame_OnNavigated(object sender, NavigationEventArgs e)
        {
            
        }
    }
}