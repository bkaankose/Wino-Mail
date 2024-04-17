using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Messages.Mails;

namespace Wino.Views.ImapSetup
{
    public sealed partial class AutoDiscoveryPage : Page
    {
        private readonly IAutoDiscoveryService _autoDiscoveryService;
        public AutoDiscoveryPage()
        {
            InitializeComponent();
            _autoDiscoveryService = App.Current.Services.GetService<IAutoDiscoveryService>();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            AutoDiscoverySettings discoverySettings = null;

            if (e.Parameter is AutoDiscoveryMinimalSettings userMinimalSettings)
            {
                discoverySettings = await _autoDiscoveryService.GetAutoDiscoverySettings(userMinimalSettings);

                await Task.Delay(1000);

                if (discoverySettings == null)
                {
                    // Couldn't find settings.

                    WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested(typeof(WelcomeImapSetupPage), "Couldn't find mailbox settings for {userMinimalSettings.Email}. Please configure it manually."));
                }
                else
                {
                    // Settings are found. Test the connection with the given password.

                    discoverySettings.UserMinimalSettings = userMinimalSettings;

                    WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(TestingImapConnectionPage), discoverySettings));
                }
            }
        }
    }
}
