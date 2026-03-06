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
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class SpecialImapCredentialsPageViewModel : MailBaseViewModel
{
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
    public partial int SelectedCalendarModeIndex { get; set; }

    [ObservableProperty]
    public partial bool CanProceed { get; set; }

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
        INativeAppService nativeAppService,
        WelcomeWizardContext wizardContext)
    {
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

        OnPropertyChanged(nameof(AppPasswordHelpUrl));
        OnPropertyChanged(nameof(CalendarModeCalDavDescription));

        Validate();
    }

    partial void OnDisplayNameChanged(string value) => Validate();
    partial void OnEmailAddressChanged(string value) => Validate();
    partial void OnAppSpecificPasswordChanged(string value) => Validate();

    private void Validate()
    {
        CanProceed = !string.IsNullOrWhiteSpace(DisplayName)
            && !string.IsNullOrWhiteSpace(EmailAddress)
            && EmailValidation.EmailValidator.Validate(EmailAddress ?? string.Empty)
            && !string.IsNullOrWhiteSpace(AppSpecificPassword);
    }

    [RelayCommand]
    private void Proceed()
    {
        if (!CanProceed) return;

        WizardContext.DisplayName = DisplayName?.Trim();
        WizardContext.EmailAddress = EmailAddress?.Trim();
        WizardContext.AppSpecificPassword = AppSpecificPassword?.Trim();
        WizardContext.CalendarSupportMode = SelectedCalendarModeIndex switch
        {
            1 => ImapCalendarSupportMode.CalDav,
            2 => ImapCalendarSupportMode.LocalOnly,
            _ => ImapCalendarSupportMode.Disabled
        };

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
