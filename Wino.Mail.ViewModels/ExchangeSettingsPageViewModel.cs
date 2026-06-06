using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// On-premises Exchange (EWS) account-setup page. Collects the EWS endpoint and
/// NTLM credentials, then reuses the shared account-creation flow (the account is
/// created with ProviderType.Exchange + CustomServerInformation by
/// AccountSetupProgressPageViewModel). OAuth / ExSTS modern auth arrives in Phase 3.
/// </summary>
public partial class ExchangeSettingsPageViewModel : MailBaseViewModel
{
    private readonly WelcomeWizardContext _wizardContext;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmailAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EwsUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidationMessage { get; set; } = string.Empty;

    public ExchangeSettingsPageViewModel(WelcomeWizardContext wizardContext)
    {
        _wizardContext = wizardContext;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (string.IsNullOrWhiteSpace(EmailAddress) && !string.IsNullOrWhiteSpace(_wizardContext.EmailAddress))
            EmailAddress = _wizardContext.EmailAddress;
    }

    [RelayCommand]
    private void Save()
    {
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(DisplayName) ||
            string.IsNullOrWhiteSpace(EmailAddress) ||
            string.IsNullOrWhiteSpace(EwsUrl) ||
            string.IsNullOrWhiteSpace(Password))
        {
            ValidationMessage = "Display name, email address, EWS URL, and password are all required.";
            return;
        }

        if (!Uri.TryCreate(EwsUrl.Trim(), UriKind.Absolute, out _))
        {
            ValidationMessage = "Enter a valid EWS URL, e.g. https://mail.example.com/EWS/Exchange.asmx";
            return;
        }

        var serverInformation = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            Address = EmailAddress.Trim(),
            IncomingServer = EwsUrl.Trim(),
            IncomingServerType = CustomIncomingServerType.Exchange,
            IncomingServerUsername = EmailAddress.Trim(),
            IncomingServerPassword = Password,
            CalendarSupportMode = ImapCalendarSupportMode.Disabled
        };

        // Connectivity (and NTLM credential validity) is verified by the folder-sync
        // step inside the setup progress page; failures roll the account back.
        _wizardContext.ImapCalDavSetupResult = new ImapCalDavSetupResult
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = EmailAddress.Trim(),
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = false, // EWS calendar arrives in Phase 2
            ServerInformation = serverInformation
        };

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step3Title,
            WinoPage.AccountSetupProgressPage));
    }
}
