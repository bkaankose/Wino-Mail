using System;
using Windows.System;
using Wino.Views.Abstract;

namespace Wino.Views
{
    public sealed partial class WelcomePage : WelcomePageAbstract
    {
        public WelcomePage()
        {
            InitializeComponent();
        }

        private async void HyperlinkClicked(object sender, Microsoft.Toolkit.Uwp.UI.Controls.LinkClickedEventArgs e)
        {
            await Launcher.LaunchUriAsync(new System.Uri(e.Link));
        }
    }
}
