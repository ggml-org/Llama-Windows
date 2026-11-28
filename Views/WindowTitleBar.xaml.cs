using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LlamaApp.Views;

/// <summary>
/// The title bar Llama's windows draw in place of the system one, whose
/// default light strip clashes with the Mica backdrop.
/// </summary>
public sealed partial class WindowTitleBar : UserControl
{
    public WindowTitleBar() => InitializeComponent();

    /// <summary>The label shown next to the logo.</summary>
    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>
    /// Shows this bar and makes it <paramref name="window"/>'s title bar: the
    /// content extends into the title bar area and this bar becomes the drag
    /// region, with the system caption buttons drawn over its right end.
    /// </summary>
    public void AttachTo(Window window)
    {
        Visibility = Visibility.Visible;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(this);
    }
}
