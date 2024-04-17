using Windows.UI.Xaml;
using Wino.Mail.ViewModels;

namespace Wino.Views.Abstract
{
    public class SettingsPageBase<T> : BasePage<T> where T : BaseViewModel
    {
        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsPageBase<T>), new PropertyMetadata(string.Empty));
    }
}
