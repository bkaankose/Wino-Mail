using Microsoft.UI.Xaml;

namespace Wino.Views.Mail;

public interface IPopupWindowAwarePage
{
    UIElement? GetPopupTitleBarElement();
    void OnPopupWindowStateChanged(bool isOpenedInPopupWindow);
}
