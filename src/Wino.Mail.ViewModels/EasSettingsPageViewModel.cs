using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using CommunityToolkit.Mvvm.Messaging;

namespace Wino.Mail.ViewModels;

public partial class EasSettingsPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accounts;
    private readonly IEasConnectionService _connection;
    private readonly ISynchronizerFactory _synchronizers;
    private readonly WelcomeWizardContext _wizard;
    private MailAccount _account;

    [ObservableProperty] public partial string Endpoint { get; set; } = string.Empty;
    [ObservableProperty] public partial string Username { get; set; } = string.Empty;
    [ObservableProperty] public partial string Password { get; set; } = string.Empty;
    [ObservableProperty] public partial string EmailAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial string SenderName { get; set; } = string.Empty;
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    public bool IsCreateMode => _account == null;

    public EasSettingsPageViewModel(IAccountService accounts, IEasConnectionService connection,
        ISynchronizerFactory synchronizers, WelcomeWizardContext wizard)
    {
        _accounts = accounts; _connection = connection; _synchronizers = synchronizers; _wizard = wizard;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        try
        {
            if (parameters is Guid accountId)
            {
                _account = await _accounts.GetAccountAsync(accountId) ?? throw new InvalidOperationException("The EAS account no longer exists.");
                if (_account.ProviderType != MailProviderType.EAS)
                    throw new InvalidOperationException("The selected account is not an EAS account.");
                Load(_account.ServerInformation);
                EmailAddress = _account.Address;
                SenderName = _account.SenderName;
            }
            else
            {
                _account = null;
                if (_wizard.EasServerInformation != null)
                    Load(_wizard.EasServerInformation);
                EmailAddress = _wizard.EmailAddress ?? string.Empty;
                SenderName = _wizard.DisplayName ?? string.Empty;
            }
            OnPropertyChanged(nameof(IsCreateMode));
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    private void Load(CustomServerInformation server)
    {
        Endpoint = server.IncomingServer;
        Username = server.IncomingServerUsername;
        Password = server.IncomingServerPassword ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = string.Empty;
        if (!MailAccountAddressValidator.IsValid(EmailAddress) || string.IsNullOrWhiteSpace(SenderName) ||
            string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password) ||
            !Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            ErrorMessage = Translator.EAS_InvalidSettings;
            return;
        }

        try
        {
            var senderName = SenderName.Trim();
            var server = _account?.ServerInformation.CloneForCredentialStorage() ?? new CustomServerInformation { Id = Guid.NewGuid() };
            server.IncomingServer = endpoint.AbsoluteUri;
            server.IncomingServerType = CustomIncomingServerType.EAS;
            server.IncomingServerUsername = Username.Trim();
            server.IncomingServerPassword = Password;
            server.Address = EmailAddress.Trim();
            await _connection.ValidateAsync(server, _account?.Id ?? _wizard.EasAccountId, cancellationToken);
            if (_account == null)
            {
                _wizard.EasServerInformation = server;
                _wizard.EmailAddress = server.Address;
                _wizard.DisplayName = senderName;
                server.CalendarSupportMode = _wizard.CalendarIntegrationSource == AccountIntegrationSource.Local
                    ? ImapCalendarSupportMode.LocalOnly : ImapCalendarSupportMode.Disabled;
                _wizard.ImapCalDavSetupResult = new ImapCalDavSetupResult
                {
                    DisplayName = senderName, EmailAddress = server.Address, ServerInformation = server,
                    IsMailAccessGranted = _wizard.IsMailAccessEnabled,
                    IsCalendarAccessGranted = _wizard.IsCalendarAccessEnabled && _wizard.CalendarIntegrationSource == AccountIntegrationSource.Provider,
                    ShouldAppendMessagesToSentFolder = false
                };
                Messenger.Send(new BreadcrumbNavigationRequested(Translator.WelcomeWizard_Step3Title, WinoPage.AccountSetupProgressPage));
            }
            else
            {
                await _accounts.UpdateAccountCustomServerInformationAsync(server);
                await _synchronizers.DeleteSynchronizerAsync(_account.Id);
                Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }
}
