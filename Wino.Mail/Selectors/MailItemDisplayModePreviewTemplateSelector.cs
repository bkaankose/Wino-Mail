using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Selectors
{
    /// <summary>
    /// Template selector for previewing mail item display modes in Settings->Personalization page.
    /// </summary>
    public partial class MailItemDisplayModePreviewTemplateSelector : DataTemplateSelector
    {
        public DataTemplate CompactTemplate { get; set; }
        public DataTemplate MediumTemplate { get; set; }
        public DataTemplate SpaciousTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MailListDisplayMode mode)
            {
                switch (mode)
                {
                    case MailListDisplayMode.Spacious:
                        return SpaciousTemplate;
                    case MailListDisplayMode.Medium:
                        return MediumTemplate;
                    case MailListDisplayMode.Compact:
                        return CompactTemplate;
                }
            }

            return base.SelectTemplateCore(item, container);
        }
    }
}
