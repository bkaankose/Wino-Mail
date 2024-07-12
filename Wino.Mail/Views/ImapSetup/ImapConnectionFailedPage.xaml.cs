using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Messages.Mails;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
#else
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
#endif

namespace Wino.Views.ImapSetup
{
    public sealed partial class ImapConnectionFailedPage : Page
    {
        private string _protocolLog;

        private readonly IClipboardService _clipboardService = App.Current.Services.GetService<IClipboardService>();
        private readonly IDialogService _dialogService = App.Current.Services.GetService<IDialogService>();

        public ImapConnectionFailedPage()
        {
            InitializeComponent();
        }

        private async void CopyProtocolLogButtonClicked(object sender, RoutedEventArgs e)
        {
            await _clipboardService.CopyClipboardAsync(_protocolLog);

            _dialogService.InfoBarMessage(Translator.ClipboardTextCopied_Title, string.Format(Translator.ClipboardTextCopied_Message, "Log"), Core.Domain.Enums.InfoBarMessageType.Information);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ImapConnectionFailedPackage failedPackage)
            {
                ConnectionFailedMessage.Text = failedPackage.GetErrorMessage();

                ProtocolLogGrid.Visibility = !string.IsNullOrEmpty(failedPackage.ProtocolLog) ? Visibility.Visible : Visibility.Collapsed;
                _protocolLog = failedPackage.ProtocolLog;
            }
        }

        private void TryAgainClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested());

        private void CloseClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new ImapSetupDismissRequested());
    }
}
