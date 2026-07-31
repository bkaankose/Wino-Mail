using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Controls;

internal static class MenuFlyoutLanguageHelper
{
    private const string ChineseLanguageTag = "zh-CN";

    public static void Apply(DependencyObject? element)
    {
        if (element == null)
        {
            return;
        }

        if (WinoApplication.Current.Services.GetRequiredService<IPreferencesService>().CurrentLanguage == AppLanguage.Chinese)
        {
            switch (element)
            {
                case MenuFlyoutItemBase menuFlyoutItem:
                    menuFlyoutItem.Language = ChineseLanguageTag;
                    break;
                case FrameworkElement frameworkElement:
                    frameworkElement.Language = ChineseLanguageTag;
                    break;
            }
        }
        else
        {
            switch (element)
            {
                case MenuFlyoutItemBase menuFlyoutItem:
                    menuFlyoutItem.ClearValue(MenuFlyoutItemBase.LanguageProperty);
                    break;
                case FrameworkElement frameworkElement:
                    frameworkElement.ClearValue(FrameworkElement.LanguageProperty);
                    break;
            }
        }
    }
}
