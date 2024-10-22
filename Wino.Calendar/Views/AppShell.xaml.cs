using Windows.UI.Xaml.Controls;
using Wino.Calendar.Views.Abstract;

namespace Wino.Calendar.Views
{
    public sealed partial class AppShell : AppShellAbstract
    {
        public Frame GetShellFrame() => ShellFrame;
        public AppShell()
        {
            InitializeComponent();

        }

        private void ShellFrameContentNavigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {

        }

        private void BackButtonClicked(Core.UWP.Controls.WinoAppTitleBar sender, Windows.UI.Xaml.RoutedEventArgs args)
        {

        }
    }
}
