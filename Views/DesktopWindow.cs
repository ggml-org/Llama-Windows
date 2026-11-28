using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace LlamaApp.Views;

internal static class DesktopWindow
{
    // Restore minimized windows without disturbing maximized ones.
    public static void Show(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();

        window.AppWindow.Show();
        window.Activate();
    }
}
