using Wino.Views.Abstract;

#if NET8_0
using Microsoft.UI.Xaml.Navigation;
#else
using Windows.UI.Xaml.Navigation;
#endif

namespace Wino.Views
{
    public sealed partial class AccountManagementPage : AccountManagementPageAbstract
    {
        public AccountManagementPage()
        {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Enabled;
        }
    }
}
