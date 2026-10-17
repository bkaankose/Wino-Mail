using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using WinRT;

namespace Wino.Mail.WinUI.Controls;

internal static class MenuFlyoutLanguageHelper
{
    private const string ChineseSimplifiedLanguageTag = "zh-CN";
    private const string ChineseTraditionalLanguageTag = "zh-TW";

    [DynamicWindowsRuntimeCast(typeof(MenuFlyoutItemBase))]
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public static void Apply(DependencyObject? element)
    {
        if (element == null)
        {
            return;
        }

        var currentLanguage = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>().CurrentLanguage;

        string? targetLanguageTag = currentLanguage switch
        {
            AppLanguage.ChineseSimplified => ChineseSimplifiedLanguageTag,
            AppLanguage.ChineseTraditional => ChineseTraditionalLanguageTag,
            _ => null
        };

        if (targetLanguageTag != null)
        {
            switch (element)
            {
                case MenuFlyoutItemBase menuFlyoutItem:
                    menuFlyoutItem.Language = targetLanguageTag;
                    break;
                case FrameworkElement frameworkElement:
                    frameworkElement.Language = targetLanguageTag;
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
