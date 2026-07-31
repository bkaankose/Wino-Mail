using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Controls;

public partial class WinoMenuFlyout : MenuFlyout
{
    public WinoMenuFlyout()
    {
        Opening += OnOpening;
    }

    private void OnOpening(object? sender, object e)
    {
        foreach (var item in Items)
        {
            ApplyLanguage(item);
        }
    }

    private static void ApplyLanguage(MenuFlyoutItemBase item)
    {
        MenuFlyoutLanguageHelper.Apply(item);

        if (item is MenuFlyoutSubItem subItem)
        {
            foreach (var childItem in subItem.Items)
            {
                ApplyLanguage(childItem);
            }
        }
    }
}
