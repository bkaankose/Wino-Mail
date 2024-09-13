using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Server;

namespace Wino.Dialogs
{
    public sealed partial class AccountCreationDialog : BaseAccountCreationDialog, IRecipient<CopyAuthURLRequested>
    {
        private string copyClipboardURL;
        public AccountCreationDialog()
        {
            InitializeComponent();

            WeakReferenceMessenger.Default.Register(this);
        }

        public async void Receive(CopyAuthURLRequested message)
        {
            copyClipboardURL = message.AuthURL;

            await Task.Delay(2000);

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                AuthHelpDialogButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            });
        }

        private void CancelClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e) => Complete(true);

        private async void CopyClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(copyClipboardURL)) return;

            var clipboardService = App.Current.Services.GetService<IClipboardService>();
            await clipboardService.CopyClipboardAsync(copyClipboardURL);
        }
    }
}
