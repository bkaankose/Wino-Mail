using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class SpecialImapCredentialsPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IDialogServiceBase _dialogService;
    private static readonly Dictionary<SpecialImapProvider, string> AppPasswordHelpLinks = new()
    {
        { SpecialImapProvider.iCloud, "https://support.apple.com/en-us/102654" },
        { SpecialImapProvider.Yahoo, "http://help.yahoo.com/kb/SLN15241.html" },
    };

    private readonly INativeAppService _nativeAppService;

    public WelcomeWizardContext WizardContext { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string EmailAddress { get; set; }

    [ObservableProperty]
    public partial string AppSpecificPassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresAppSpecificPassword))]
    public partial int SelectedCalendarModeIndex { get; set; }

    [ObservableProperty]
    public partial bool CanProceed { get; set; }

    public bool IsCalendarModeSelectionVisible => WizardContext.IsCalendarAccessEnabled;
    public bool RequiresAppSpecificPassword => WizardContext.IsMailAccessEnabled || SelectedCalendarModeIndex == 1;

    public string AppPasswordHelpUrl
    {
        get
        {
            if (WizardContext.SelectedProvider == null) return null;
            AppPasswordHelpLinks.TryGetValue(WizardContext.SelectedProvider.SpecialImapProvider, out var url);
            return url;
        }
    }

    public string CalendarModeCalDavDescription
        => WizardContext.SelectedProvider?.SpecialImapProvider == SpecialImapProvider.iCloud
            ? Translator.ProviderSelection_CalendarMode_CalDavDescription_Apple
            : Translator.ProviderSelection_CalendarMode_CalDavDescription_Yahoo;

    public SpecialImapCredentialsPageViewModel(
        IAccountService accountService,
        IDialogServiceBase dialogService,
        INativeAppService nativeAppService,
        WelcomeWizardContext wizardContext)
    {
        _accountService = accountService;
        _dialogService = dialogService;
        _nativeAppService = nativeAppService;
        WizardContext = wizardContext;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        // Restore from context when navigating back
        DisplayName = WizardContext.DisplayName;
        EmailAddress = WizardContext.EmailAddress;
        AppSpecificPassword = WizardContext.AppSpecificPassword;

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

        OnPropertyChanged(nameof(AppPasswordHelpUrl));
        OnPropertyChanged(nameof(CalendarModeCalDavDescription));
        OnPropertyChanged(nameof(IsCalendarModeSelectionVisible));
        OnPropertyChanged(nameof(RequiresAppSpecificPassword));

        Validate();
    }

    partial void OnDisplayNameChanged(string value) => Validate();
    partial void OnEmailAddressChanged(string value) => Validate();
    partial void OnAppSpecificPasswordChanged(string value) => Validate();
    partial void OnSelectedCalendarModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RequiresAppSpecificPassword));
        Validate();
    }

    private void Validate()
    {
        CanProceed = !string.IsNullOrWhiteSpace(DisplayName)
            && !string.IsNullOrWhiteSpace(EmailAddress)
            && MailAccountAddressValidator.IsValid(EmailAddress)
            && (!RequiresAppSpecificPassword || !string.IsNullOrWhiteSpace(AppSpecificPassword));
    }

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
        WizardContext.CalendarSupportMode = WizardContext.IsCalendarAccessEnabled
            ? SelectedCalendarModeIndex switch
            {
                1 => ImapCalendarSupportMode.CalDav,
                2 => ImapCalendarSupportMode.LocalOnly,
                _ => ImapCalendarSupportMode.Disabled
            }
            : ImapCalendarSupportMode.Disabled;

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step3Title,
            WinoPage.AccountSetupProgressPage));
    }

    [RelayCommand]
    private async Task OpenAppPasswordHelp()
    {
        var url = AppPasswordHelpUrl;
        if (url != null)
            await _nativeAppService.LaunchUriAsync(new Uri(url));
    }
}
