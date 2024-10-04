using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.UWP.Models.Personalization;

namespace Wino.Core.UWP.Selectors
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
