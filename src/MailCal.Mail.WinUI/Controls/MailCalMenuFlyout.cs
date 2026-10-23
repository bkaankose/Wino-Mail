using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace MailCal.Mail.WinUI.Controls;

public partial class MailCalMenuFlyout : MenuFlyout
{
    public MailCalMenuFlyout()
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

    [DynamicWindowsRuntimeCast(typeof(MenuFlyoutSubItem))]
    [DynamicWindowsRuntimeCast(typeof(SplitMenuFlyoutItem))]
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
        else if (item is SplitMenuFlyoutItem splitItem)
        {
            foreach (var childItem in splitItem.Items)
            {
                ApplyLanguage(childItem);
            }
        }
    }
}
