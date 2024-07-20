
using Wino.Core.UWP.Models.Personalization;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#endif

namespace Wino.Selectors
{
    public class AppThemePreviewTemplateSelector : DataTemplateSelector
    {
        public DataTemplate SystemThemeTemplate { get; set; }
        public DataTemplate PreDefinedThemeTemplate { get; set; }
        public DataTemplate CustomAppTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is SystemAppTheme)
                return SystemThemeTemplate;
            else if (item is PreDefinedAppTheme)
                return PreDefinedThemeTemplate;
            else if (item is CustomAppTheme)
                return CustomAppTemplate;

            return base.SelectTemplateCore(item);
        }
    }
}
