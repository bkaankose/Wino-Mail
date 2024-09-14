using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Messaging.Client.Mails;


namespace Wino.Views.ImapSetup
{
    public sealed partial class AdvancedImapSetupPage : Page
    {
        public List<ImapAuthenticationMethodModel> AvailableAuthenticationMethods { get; } = new List<ImapAuthenticationMethodModel>()
        {
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.Auto, Translator.ImapAuthenticationMethod_Auto),
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.None, Translator.ImapAuthenticationMethod_None),
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.NormalPassword, Translator.ImapAuthenticationMethod_Plain),
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.EncryptedPassword, Translator.ImapAuthenticationMethod_EncryptedPassword),
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.Ntlm, Translator.ImapAuthenticationMethod_Ntlm),
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.CramMd5, Translator.ImapAuthenticationMethod_CramMD5),
            new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.DigestMd5, Translator.ImapAuthenticationMethod_DigestMD5)
        };

        public List<ImapConnectionSecurityModel> AvailableConnectionSecurities { get; set; } = new List<ImapConnectionSecurityModel>()
        {
            new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.Auto, Translator.ImapConnectionSecurity_Auto),
            new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.SslTls, Translator.ImapConnectionSecurity_SslTls),
            new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.StartTls, Translator.ImapConnectionSecurity_StartTls),
            new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.None, Translator.ImapConnectionSecurity_None)
        };

        public bool UseSameCredentialsForSending
        {
            get { return (bool)GetValue(UseSameCredentialsForSendingProperty); }
            set { SetValue(UseSameCredentialsForSendingProperty, value); }
        }

        public static readonly DependencyProperty UseSameCredentialsForSendingProperty = DependencyProperty.Register(nameof(UseSameCredentialsForSending), typeof(bool), typeof(AdvancedImapSetupPage), new PropertyMetadata(true, OnUseSameCredentialsForSendingChanged));

        public AdvancedImapSetupPage()
        {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        private static void OnUseSameCredentialsForSendingChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is AdvancedImapSetupPage page)
            {
                page.UpdateOutgoingAuthenticationPanel();
            }
        }

        private void UpdateOutgoingAuthenticationPanel()
        {
            if (UseSameCredentialsForSending)
            {
                OutgoingUsernameBox.Text = UsernameBox.Text;
                OutgoingPasswordBox.Password = PasswordBox.Password;
            }
            else
            {
                OutgoingUsernameBox.Text = string.Empty;
                OutgoingPasswordBox.Password = string.Empty;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Don't override settings on back scenarios.
            // User is trying to try again the same configuration.

            if (e.NavigationMode == NavigationMode.Back) return;

            // Connection is succesfull but error occurred.
            // Imap and Smptp settings exists here at this point.

            if (e.Parameter is AutoDiscoverySettings preDefinedSettings && preDefinedSettings.UserMinimalSettings != null)
            {
                // TODO: Auto discovery settings adjustments.

                UsernameBox.Text = preDefinedSettings.UserMinimalSettings.Email;
                AddressBox.Text = preDefinedSettings.UserMinimalSettings.Email;
                DisplayNameBox.Text = preDefinedSettings.UserMinimalSettings.DisplayName;
                PasswordBox.Password = preDefinedSettings.UserMinimalSettings.Password;

                var serverInfo = preDefinedSettings.ToServerInformation();

                IncomingServerBox.Text = serverInfo.IncomingServer;
                IncomingServerPortBox.Text = serverInfo.IncomingServerPort;

                OutgoingPasswordBox.Password = serverInfo.OutgoingServerPassword;
                OutgoingServerPort.Text = serverInfo.OutgoingServerPort;
                OutgoingUsernameBox.Text = serverInfo.OutgoingServerUsername;

                UseSameCredentialsForSending = OutgoingUsernameBox.Text == UsernameBox.Text;
            }
            else if (e.Parameter is AutoDiscoveryMinimalSettings autoDiscoveryMinimalSettings)
            {
                // Auto discovery failed. Only minimal settings are passed.

                UsernameBox.Text = autoDiscoveryMinimalSettings.Email;
                AddressBox.Text = autoDiscoveryMinimalSettings.Email;
                DisplayNameBox.Text = autoDiscoveryMinimalSettings.DisplayName;
                PasswordBox.Password = autoDiscoveryMinimalSettings.Password;
            }
        }

        private void CancelClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new ImapSetupDismissRequested(null));

        private string GetServerWithoutPort(string server)
        {
            var splitted = server.Split(':');

            if (splitted.Length > 1)
            {
                return splitted[0];
            }

            return server;
        }

        private void SignInClicked(object sender, RoutedEventArgs e)
        {
            var info = new CustomServerInformation()
            {
                IncomingServer = GetServerWithoutPort(IncomingServerBox.Text),
                Id = Guid.NewGuid(),

                IncomingServerPassword = PasswordBox.Password,
                IncomingServerType = Core.Domain.Enums.CustomIncomingServerType.IMAP4,
                IncomingServerUsername = UsernameBox.Text,
                IncomingAuthenticationMethod = (IncomingAuthenticationMethod.SelectedItem as ImapAuthenticationMethodModel).ImapAuthenticationMethod,
                IncomingServerSocketOption = (IncomingConnectionSecurity.SelectedItem as ImapConnectionSecurityModel).ImapConnectionSecurity,
                IncomingServerPort = IncomingServerPortBox.Text,

                OutgoingServer = GetServerWithoutPort(OutgoingServerBox.Text),
                OutgoingServerPort = OutgoingServerPort.Text,
                OutgoingServerPassword = OutgoingPasswordBox.Password,
                OutgoingAuthenticationMethod = (OutgoingAuthenticationMethod.SelectedItem as ImapAuthenticationMethodModel).ImapAuthenticationMethod,
                OutgoingServerSocketOption = (OutgoingConnectionSecurity.SelectedItem as ImapConnectionSecurityModel).ImapConnectionSecurity,
                OutgoingServerUsername = OutgoingUsernameBox.Text,

                ProxyServer = ProxyServerBox.Text,
                ProxyServerPort = ProxyServerPortBox.Text,
                Address = AddressBox.Text,
                DisplayName = DisplayNameBox.Text,
                MaxConcurrentClients = 5
            };

            if (UseSameCredentialsForSending)
            {
                info.OutgoingServerUsername = info.IncomingServerUsername;
                info.OutgoingServerPassword = info.IncomingServerPassword;
            }
            else
            {
                info.OutgoingServerUsername = OutgoingUsernameBox.Text;
                info.OutgoingServerPassword = OutgoingPasswordBox.Password;
            }

            WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(TestingImapConnectionPage), info));
        }

        private void IncomingServerChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox senderTextBox)
            {
                var splitted = senderTextBox.Text.Split(':');

                if (splitted.Length > 1)
                {
                    IncomingServerPortBox.Text = splitted[splitted.Length - 1];
                }
            }
        }

        private void OutgoingServerChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox senderTextBox)
            {
                var splitted = senderTextBox.Text.Split(':');

                if (splitted.Length > 1)
                {
                    OutgoingServerPort.Text = splitted[splitted.Length - 1];
                }
            }
        }

        private void IncomingUsernameChanged(object sender, TextChangedEventArgs e)
        {
            if (UseSameCredentialsForSending)
            {
                OutgoingUsernameBox.Text = UsernameBox.Text;
            }
        }

        private void IncomingPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (UseSameCredentialsForSending)
            {
                OutgoingPasswordBox.Password = PasswordBox.Password;
            }
        }
    }
}
