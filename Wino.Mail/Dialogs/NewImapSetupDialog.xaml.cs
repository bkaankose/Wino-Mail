﻿using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Mails;
using Wino.Views.ImapSetup;

namespace Wino.Dialogs
{
    public enum ImapSetupState
    {
        Welcome,
        AutoDiscovery,
        TestingConnection,
        PreparingFolder
    }

    public sealed partial class NewImapSetupDialog : ContentDialog,
        IRecipient<ImapSetupNavigationRequested>,
        IRecipient<ImapSetupBackNavigationRequested>,
        IRecipient<ImapSetupDismissRequested>,
        ICustomServerAccountCreationDialog
    {
        private TaskCompletionSource<CustomServerInformation> _getServerInfoTaskCompletionSource = new TaskCompletionSource<CustomServerInformation>();

        private bool isDismissRequested = false;

        public NewImapSetupDialog()
        {
            InitializeComponent();
        }

        // Not used for now.
        public AccountCreationDialogState State { get; set; }

        public void Complete(bool cancel)
        {
            if (!_getServerInfoTaskCompletionSource.Task.IsCompleted)
                _getServerInfoTaskCompletionSource.TrySetResult(null);

            isDismissRequested = true;

            Hide();
        }

        public Task<CustomServerInformation> GetCustomServerInformationAsync() => _getServerInfoTaskCompletionSource.Task;

        public async void Receive(ImapSetupBackNavigationRequested message)
        {
            // Frame go back
            if (message.PageType == null)
            {
                if (ImapFrame.CanGoBack)
                {
                    // Go back using Dispatcher to allow navigations in OnNavigatedTo.
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        ImapFrame.GoBack();
                    });
                }
            }
            else
            {
                ImapFrame.Navigate(message.PageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromLeft });
            }
        }

        public void Receive(ImapSetupNavigationRequested message)
        {
            ImapFrame.Navigate(message.PageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }

        public void Receive(ImapSetupDismissRequested message) => _getServerInfoTaskCompletionSource.TrySetResult(message.CompletedServerInformation);

        public void ShowDialog(CancellationTokenSource cancellationTokenSource)
            => _ = ShowAsync();

        public void ShowPreparingFolders()
        {
            ImapFrame.Navigate(typeof(PreparingImapFoldersPage), new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromLeft });
        }

        public void StartImapConnectionSetup(MailAccount account) => ImapFrame.Navigate(typeof(WelcomeImapSetupPage), account, new DrillInNavigationTransitionInfo());


        private void ImapSetupDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => WeakReferenceMessenger.Default.UnregisterAll(this);

        private void ImapSetupDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => WeakReferenceMessenger.Default.RegisterAll(this);

        // Don't hide the dialog unless dismiss is requested from the inner pages specifically.
        private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = !isDismissRequested;
    }
}
