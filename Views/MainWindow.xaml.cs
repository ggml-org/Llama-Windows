using System.Reflection;
using Microsoft.UI.Xaml.Navigation;

namespace LlamaApp.Views
{
    /// <summary>
    /// Main application shell: custom title bar + navigation menu + content frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Initial selection raises SelectionChanged, which navigates to the page.
            MenuControl.SelectedItem = MenuControl.MenuItems.OfType<NavigationViewItem>().First();
        }

        /// <summary>
        /// Triggered when the user picks a menu item. Resolves the target page from
        /// the item's Tag and navigates the content frame to it. This is the event
        /// that actually changes the view.
        /// </summary>
        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
            
            var pageType = GetType().Assembly.GetType($"LlamaApp.Views.{tag}");
            if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType, null,
                    new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            }
        }

        /// <summary>
        /// Runs after the content frame has navigated to a new page. Keeps the
        /// navigation menu selection in sync with the current page, which matters
        /// when navigation is triggered programmatically (e.g. from a page button)
        /// rather than from a menu click.
        /// </summary>
        private void ContentFrame_OnNavigated(object sender, NavigationEventArgs e)
        {
            if (e.SourcePageType is null) return;

            foreach (var item in MenuControl.MenuItems.OfType<NavigationViewItem>()
                                        .Concat(MenuControl.FooterMenuItems.OfType<NavigationViewItem>()))
            {
                if (item.Tag is not string tag
                    || GetType().Assembly.GetType($"LlamaApp.Views.{tag}") != e.SourcePageType) continue;
                MenuControl.SelectedItem = item;
                return;
            }
        }
    }
}