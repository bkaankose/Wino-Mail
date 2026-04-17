using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Controls;

internal static class MenuFlyoutLanguageHelper
{
    private const string ChineseLanguageTag = "zh-CN";

    public static void Apply(MenuFlyoutItemBase item)
    {
        if (WinoApplication.Current.Services.GetRequiredService<IPreferencesService>().CurrentLanguage == AppLanguage.Chinese)
        {
            item.Language = ChineseLanguageTag;
        }
        else
        {
            item.ClearValue(MenuFlyoutItemBase.LanguageProperty);
        }
    }
}
