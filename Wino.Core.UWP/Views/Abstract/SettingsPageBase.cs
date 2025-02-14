using Windows.UI.Xaml;
using Wino.Core.UWP;
using Wino.Core.ViewModels;

namespace Wino.Views.Abstract
{
    public partial class SettingsPageBase<T> : BasePage<T> where T : CoreBaseViewModel
    {
        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsPageBase<T>), new PropertyMetadata(string.Empty));
    }
}
