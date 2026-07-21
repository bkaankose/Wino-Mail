using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Mail.Uwp.Models.Personalization;

namespace Wino.Mail.Uwp.Selectors;

public partial class AppThemePreviewTemplateSelector : DataTemplateSelector
{
    public DataTemplate SystemThemeTemplate { get; set; } = null!;
    public DataTemplate PreDefinedThemeTemplate { get; set; } = null!;
    public DataTemplate CustomAppTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is SystemAppTheme)
            return SystemThemeTemplate;
        else if (item is PreDefinedAppTheme)
            return PreDefinedThemeTemplate;
        else if (item is CustomAppTheme)
            return CustomAppTemplate;

        return base.SelectTemplateCore(item) ?? SystemThemeTemplate;
    }
}
