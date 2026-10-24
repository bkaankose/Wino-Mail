using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class SpecialImapCredentialsPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IDialogServiceBase _dialogService;
    private readonly IKnownImapProviderCatalog _knownImapProviderCatalog;

    private readonly IExternalLauncher _externalLauncher;

    public WelcomeWizardContext WizardContext { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string EmailAddress { get; set; }

    [ObservableProperty]
    public partial string AppSpecificPassword { get; set; }

    [ObservableProperty]
    public partial string LoginAddress { get; set; }

    [ObservableProperty]
    public partial bool IsCustomDomain { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresAppSpecificPassword))]
    public partial int SelectedCalendarModeIndex { get; set; }

    [ObservableProperty]
    public partial bool CanProceed { get; set; }

    /// <summary>
    /// Data-center choices for providers such as Zoho. Empty for everyone else.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegionSelectionVisible))]
    public partial List<KnownImapProviderRegion> Regions { get; set; } = [];

    [ObservableProperty]
    public partial KnownImapProviderRegion SelectedRegion { get; set; }

    public bool IsRegionSelectionVisible => Regions.Count > 0;

    public bool IsCalendarModeSelectionVisible => WizardContext.IsCalendarAccessEnabled;
    public bool RequiresAppSpecificPassword => WizardContext.IsMailAccessEnabled || SelectedCalendarModeIndex == 1;

    /// <summary>
    /// The catalog entry behind the selected provider, or null outside the catalog.
    /// </summary>
    private KnownImapProviderDefinition ProviderDefinition => WizardContext.SelectedProvider == null
        ? null
        : _knownImapProviderCatalog.GetBySpecialProvider(WizardContext.SelectedProvider.SpecialImapProvider);

    public string ProviderName => WizardContext.SelectedProvider?.Name ?? string.Empty;

    public string AppPasswordHelpUrl => ProviderDefinition?.AppPasswordHelpUrl;

    public bool IsHelpLinkVisible => !string.IsNullOrWhiteSpace(AppPasswordHelpUrl);

    /// <summary>
    /// The password field title follows what the provider actually expects there.
    /// </summary>
    public string PasswordHeader => ProviderDefinition?.PasswordKind switch
    {
        KnownImapPasswordKind.BridgePassword => Translator.ProviderSelection_PasswordHeader_Bridge,
        KnownImapPasswordKind.AccountPassword => Translator.ProviderSelection_PasswordHeader_Account,
        _ => Translator.ProviderSelection_AppPasswordHeader
    };

    public string HelpLinkText => ProviderDefinition?.PasswordKind == KnownImapPasswordKind.AppPassword
        ? Translator.ProviderSelection_AppPasswordHelp
        : Translator.ProviderSelection_SetupHint_Help;

    /// <summary>
    /// The one thing the user must do on the provider's side before these credentials work.
    /// Empty when the provider needs nothing special.
    /// </summary>
    public string SetupHint
    {
        get
        {
            var definition = ProviderDefinition;
            if (definition == null) return string.Empty;

            var format = definition.SetupHint switch
            {
                KnownImapSetupHint.AppPasswordRequired => Translator.ProviderSelection_SetupHint_AppPasswordRequired,
                KnownImapSetupHint.ImapDisabledByDefault => Translator.ProviderSelection_SetupHint_ImapDisabledByDefault,
                KnownImapSetupHint.LocalBridgeRequired => Translator.ProviderSelection_SetupHint_LocalBridgeRequired_Device,
                KnownImapSetupHint.AuthorizationCodeRequired => Translator.ProviderSelection_SetupHint_AuthorizationCodeRequired,
                KnownImapSetupHint.ThirdPartyAccessRequired => Translator.ProviderSelection_SetupHint_ThirdPartyAccessRequired,
                KnownImapSetupHint.SeparateEmailPasswordRequired => Translator.ProviderSelection_SetupHint_SeparateEmailPasswordRequired,
                KnownImapSetupHint.TwoFactorCodeSuffix => Translator.ProviderSelection_SetupHint_TwoFactorCodeSuffix,
                _ => string.Empty
            };

            return string.IsNullOrEmpty(format) ? string.Empty : string.Format(format, ProviderName);
        }
    }

    public bool IsSetupHintVisible => !string.IsNullOrEmpty(SetupHint);

    /// <summary>
    /// Hints that block the connection until the user changes a provider setting read as warnings;
    /// the rest are informational.
    /// </summary>
    public bool IsSetupHintBlocking => ProviderDefinition?.SetupHint is
        KnownImapSetupHint.ImapDisabledByDefault or
        KnownImapSetupHint.LocalBridgeRequired or
        KnownImapSetupHint.ThirdPartyAccessRequired;

    public string CustomDomainMessage
    {
        get
        {
            if (!IsCustomDomain || !MailAccountAddressValidator.TryGetDomain(EmailAddress, out var domain))
                return string.Empty;

            return string.Format(Translator.ProviderSelection_CustomDomain_Message, domain, ProviderName, EmailAddress.Trim());
        }
    }

    public string LoginAddressHeader => string.Format(Translator.ProviderSelection_LoginAddressHeader, ProviderName);

    public string LoginAddressPlaceholder => ProviderDefinition?.EmailDomains.Count > 0
        ? string.Format(Translator.ProviderSelection_LoginAddressPlaceholder, ProviderDefinition.EmailDomains[0])
        : string.Empty;

    public string LoginAddressDescription => ProviderDefinition?.EmailDomains.Count > 0
        ? string.Format(Translator.ProviderSelection_LoginAddressDescription, string.Join(", ", ProviderDefinition.EmailDomains.Select(domain => $"@{domain}")))
        : string.Empty;

    public string RegionDescription => string.Format(Translator.ProviderSelection_RegionDescription, ProviderName);

    public string CalendarModeCalDavDescription
        => string.Format(Translator.ProviderSelection_CalendarMode_CalDavDescription, ProviderName);

    public SpecialImapCredentialsPageViewModel(
        IAccountService accountService,
        IDialogServiceBase dialogService,
        IExternalLauncher externalLauncher,
        WelcomeWizardContext wizardContext,
        IKnownImapProviderCatalog knownImapProviderCatalog)
    {
        _accountService = accountService;
        _dialogService = dialogService;
        _externalLauncher = externalLauncher;
        _knownImapProviderCatalog = knownImapProviderCatalog;
        WizardContext = wizardContext;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        // Restore from context when navigating back
        DisplayName = WizardContext.DisplayName;
        EmailAddress = WizardContext.EmailAddress;
        AppSpecificPassword = WizardContext.AppSpecificPassword;
        LoginAddress = WizardContext.LoginAddress;

        SelectedCalendarModeIndex = WizardContext.CalendarSupportMode switch
        {
            ImapCalendarSupportMode.CalDav => 1,
            ImapCalendarSupportMode.LocalOnly => 2,
            _ => 0
        };

        if (!WizardContext.IsCalendarAccessEnabled)
        {
            SelectedCalendarModeIndex = 0;
        }

        Regions = ProviderDefinition?.Regions?.ToList() ?? [];
        SelectedRegion = Regions.FirstOrDefault(region =>
            string.Equals(region.Id, WizardContext.RegionId, StringComparison.OrdinalIgnoreCase))
            ?? Regions.FirstOrDefault();

        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(AppPasswordHelpUrl));
        OnPropertyChanged(nameof(IsHelpLinkVisible));
        OnPropertyChanged(nameof(PasswordHeader));
        OnPropertyChanged(nameof(HelpLinkText));
        OnPropertyChanged(nameof(SetupHint));
        OnPropertyChanged(nameof(IsSetupHintVisible));
        OnPropertyChanged(nameof(IsSetupHintBlocking));
        OnPropertyChanged(nameof(RegionDescription));
        OnPropertyChanged(nameof(CalendarModeCalDavDescription));
        OnPropertyChanged(nameof(IsCalendarModeSelectionVisible));
        OnPropertyChanged(nameof(RequiresAppSpecificPassword));
        OnPropertyChanged(nameof(LoginAddressHeader));
        OnPropertyChanged(nameof(LoginAddressPlaceholder));
        OnPropertyChanged(nameof(LoginAddressDescription));

        Validate();
    }

    partial void OnDisplayNameChanged(string value) => Validate();
    partial void OnEmailAddressChanged(string value) => Validate();
    partial void OnAppSpecificPasswordChanged(string value) => Validate();
    partial void OnLoginAddressChanged(string value) => Validate();
    partial void OnSelectedRegionChanged(KnownImapProviderRegion value) => Validate();
    partial void OnSelectedCalendarModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RequiresAppSpecificPassword));
        Validate();
    }

    private void Validate()
    {
        var isEmailValid = !string.IsNullOrWhiteSpace(EmailAddress) && MailAccountAddressValidator.IsValid(EmailAddress);
        IsCustomDomain = isEmailValid && _knownImapProviderCatalog.RequiresLoginAddress(ProviderDefinition, EmailAddress);
        OnPropertyChanged(nameof(CustomDomainMessage));

        CanProceed = !string.IsNullOrWhiteSpace(DisplayName)
            && isEmailValid
            && (!IsCustomDomain || IsLoginAddressValid())
            && (!RequiresAppSpecificPassword || !string.IsNullOrWhiteSpace(AppSpecificPassword))
            && (!IsRegionSelectionVisible || SelectedRegion != null);
    }

    private bool IsLoginAddressValid()
        => !string.IsNullOrWhiteSpace(LoginAddress)
            && MailAccountAddressValidator.IsValid(LoginAddress)
            && _knownImapProviderCatalog.IsProviderDomain(ProviderDefinition, LoginAddress);

    [RelayCommand]
    private async Task ProceedAsync()
    {
        if (!CanProceed) return;

        if (await _accountService.AccountAddressExistsAsync(EmailAddress))
        {
            await _dialogService.ShowMessageAsync(
                Translator.DialogMessage_AccountAddressExistsMessage,
                Translator.DialogMessage_AccountExistsTitle,
                WinoCustomMessageDialogIcon.Warning);
            return;
        }

        WizardContext.DisplayName = DisplayName?.Trim();
        WizardContext.EmailAddress = EmailAddress?.Trim();
        WizardContext.AppSpecificPassword = AppSpecificPassword?.Trim();
        WizardContext.LoginAddress = IsCustomDomain ? LoginAddress?.Trim() : null;
        WizardContext.RegionId = SelectedRegion?.Id;
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step3Title,
            WinoPage.AccountSetupProgressPage));
    }

    [RelayCommand]
    private async Task OpenAppPasswordHelp()
    {
        var url = AppPasswordHelpUrl;
        if (url != null)
            (await _externalLauncher.LaunchUriAsync(new Uri(url))).ThrowIfNotSucceeded();
    }
}
