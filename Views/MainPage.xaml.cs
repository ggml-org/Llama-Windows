using System.IO;
using LlamaApp;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LlamaApp.Views
{
    
    
    /// <summary>
    /// A simple page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();

            UserText.Text = $"Hello, {UserHelper.DisplayName}!";
        }
    }
}