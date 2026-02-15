using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Views.Mail;

public sealed class DetachedRenderingWindow : Window
{
    private readonly IPopupWindowAwarePage? _popupWindowAwarePage;

    public DetachedRenderingWindow(UIElement content)
    {
        Title = "Wino Mail";

        Content = new Grid
        {
            Children =
            {
                content
            }
        };

        _popupWindowAwarePage = content as IPopupWindowAwarePage;
        _popupWindowAwarePage?.OnPopupWindowStateChanged(true);

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        UpdateTitleBarDragRegion();
        DispatcherQueue.TryEnqueue(UpdateTitleBarDragRegion);

        Closed += DetachedRenderingWindowClosed;
    }

    private void UpdateTitleBarDragRegion()
    {
        var titleBarElement = _popupWindowAwarePage?.GetPopupTitleBarElement();

        if (titleBarElement != null)
        {
            SetTitleBar(titleBarElement);
        }
    }

    private void DetachedRenderingWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= DetachedRenderingWindowClosed;
        _popupWindowAwarePage?.OnPopupWindowStateChanged(false);
    }
}
