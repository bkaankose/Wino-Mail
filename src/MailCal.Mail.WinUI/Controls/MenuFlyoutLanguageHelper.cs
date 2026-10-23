using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using WinRT;

namespace MailCal.Mail.WinUI.Controls;

internal static class MenuFlyoutLanguageHelper
{
    private const string ChineseSimplifiedLanguageTag = "zh-CN";
    private const string ChineseTraditionalLanguageTag = "zh-TW";

    /// <summary>
    /// Returns the language tag that menu text needs for correct CJK glyphs, or null when the default applies.
    /// </summary>
    public static string? GetLanguageTag(AppLanguage language) => language switch
    {
        AppLanguage.Chinese => ChineseSimplifiedLanguageTag,
        AppLanguage.ChineseTraditional => ChineseTraditionalLanguageTag,
        _ => null
    };

    [DynamicWindowsRuntimeCast(typeof(MenuFlyoutItemBase))]
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public static void Apply(DependencyObject? element)
    {
        if (element == null)
        {
            return;
        }

        var languageTag = GetLanguageTag(MailCalApplication.Current.Services.GetRequiredService<IPreferencesService>().CurrentLanguage);

        if (languageTag != null)
        {
            switch (element)
            {
                case MenuFlyoutItemBase menuFlyoutItem:
                    menuFlyoutItem.Language = languageTag;
                    break;
                case FrameworkElement frameworkElement:
                    frameworkElement.Language = languageTag;
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
