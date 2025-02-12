using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Selectors
{
    public partial class MailItemDisplaySelector : DataTemplateSelector
    {
        public DataTemplate SingleMailItemTemplate { get; set; }
        public DataTemplate ThreadMailItemTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MailItemViewModel)
                return SingleMailItemTemplate;
            else if (item is ThreadMailItemViewModel)
                return ThreadMailItemTemplate;

            return base.SelectTemplateCore(item, container);
        }
    }
}
