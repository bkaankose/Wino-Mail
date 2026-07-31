using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

public partial class WelcomeWizardContext : ObservableObject
{
    // Step 2 — Provider selection
    [ObservableProperty]
    public partial IProviderDetail SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string AccountColorHex { get; set; }

    [ObservableProperty]
    public partial InitialSynchronizationRange SelectedInitialSynchronizationRange { get; set; } = InitialSynchronizationRange.SixMonths;

    [ObservableProperty]
    public partial bool IsMailAccessEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCalendarAccessEnabled { get; set; } = true;

    // Special IMAP fields (iCloud/Yahoo)
    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string EmailAddress { get; set; }

    [ObservableProperty]
    public partial string AppSpecificPassword { get; set; }

    [ObservableProperty]
    public partial ImapCalendarSupportMode CalendarSupportMode { get; set; } = ImapCalendarSupportMode.Disabled;

    // Generic IMAP — populated by ImapCalDavSettingsPage
    public ImapCalDavSetupResult ImapCalDavSetupResult { get; set; }

    // Computed helpers
    public bool IsOAuthProvider => SelectedProvider?.Type is MailProviderType.Outlook or MailProviderType.Gmail;

    public bool IsSpecialImapProvider =>
        SelectedProvider?.SpecialImapProvider is SpecialImapProvider.iCloud or SpecialImapProvider.Yahoo;

    public bool IsGenericImap =>
        SelectedProvider?.Type == MailProviderType.IMAP4
        && SelectedProvider?.SpecialImapProvider == SpecialImapProvider.None;

    public SpecialImapProviderDetails BuildSpecialImapProviderDetails()
    {
        if (!IsSpecialImapProvider) return null;

        return new SpecialImapProviderDetails(
            EmailAddress,
            AppSpecificPassword,
            DisplayName,
            SelectedProvider.SpecialImapProvider,
            CalendarSupportMode);
    }

    public AccountCreationDialogResult BuildAccountCreationDialogResult()
    {
        return new AccountCreationDialogResult(
            SelectedProvider.Type,
            AccountName,
            BuildSpecialImapProviderDetails(),
            AccountColorHex,
            SelectedInitialSynchronizationRange,
            IsMailAccessEnabled,
            IsCalendarAccessEnabled);
    }

    public void Reset()
    {
        SelectedProvider = null;
        AccountName = null;
        AccountColorHex = null;
        SelectedInitialSynchronizationRange = InitialSynchronizationRange.SixMonths;
        IsMailAccessEnabled = true;
        IsCalendarAccessEnabled = true;
        DisplayName = null;
        EmailAddress = null;
        AppSpecificPassword = null;
        CalendarSupportMode = ImapCalendarSupportMode.Disabled;
        ImapCalDavSetupResult = null;
    }
}
