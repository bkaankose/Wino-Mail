using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Mail.ViewModels.Data;

public enum ImapCalDavSettingsPageMode
{
    Create,
    Edit,
    Wizard,
    AddAccount
}

public sealed class ImapCalDavSettingsNavigationContext
{
    public ImapCalDavSettingsPageMode Mode { get; init; }
    public Guid AccountId { get; init; }
    public AccountCreationDialogResult AccountCreationDialogResult { get; init; }
    public TaskCompletionSource<ImapCalDavSetupResult> CompletionSource { get; init; }

    /// <summary>Set when the address-first setup page opens Advanced setup with what it already found.</summary>
    public ImapSetupPrefill? Prefill { get; init; }

    public static ImapCalDavSettingsNavigationContext CreateForCreateMode(
        AccountCreationDialogResult accountCreationDialogResult,
        TaskCompletionSource<ImapCalDavSetupResult> completionSource)
        => new()
        {
            Mode = ImapCalDavSettingsPageMode.Create,
            AccountCreationDialogResult = accountCreationDialogResult,
            CompletionSource = completionSource
        };

    public static ImapCalDavSettingsNavigationContext CreateForEditMode(Guid accountId)
        => new()
        {
            Mode = ImapCalDavSettingsPageMode.Edit,
            AccountId = accountId
        };

    public static ImapCalDavSettingsNavigationContext CreateForWizardMode(
        AccountCreationDialogResult accountCreationDialogResult,
        ImapSetupPrefill? prefill = null)
        => new()
        {
            Mode = ImapCalDavSettingsPageMode.Wizard,
            AccountCreationDialogResult = accountCreationDialogResult,
            Prefill = prefill
        };

    public static ImapCalDavSettingsNavigationContext CreateForAddAccountMode(
        AccountCreationDialogResult accountCreationDialogResult,
        ImapSetupPrefill? prefill = null)
        => new()
        {
            Mode = ImapCalDavSettingsPageMode.AddAccount,
            AccountCreationDialogResult = accountCreationDialogResult,
            Prefill = prefill
        };

    public bool IsWizardMode => Mode == ImapCalDavSettingsPageMode.Wizard;
}

public sealed class ImapCalDavSetupResult
{
    public string DisplayName { get; init; }
    public string EmailAddress { get; init; }
    public bool IsMailAccessGranted { get; init; }
    public bool IsCalendarAccessGranted { get; init; }
    public bool ShouldAppendMessagesToSentFolder { get; init; } = true;
    public CustomServerInformation ServerInformation { get; init; }

    /// <summary>
    /// The in-memory OAuth sign-in when the account signs in with OAuth 2.0. Its key becomes the
    /// new account id, and its tokens are written only after the account is created.
    /// </summary>
    public OAuthSetupSession OAuthSession { get; init; }

    public ImapAccountSetupData ToAccountSetupData() => new()
    {
        DisplayName = DisplayName,
        EmailAddress = EmailAddress,
        IsMailAccessGranted = IsMailAccessGranted,
        IsCalendarAccessGranted = IsCalendarAccessGranted,
        ShouldAppendMessagesToSentFolder = ShouldAppendMessagesToSentFolder,
        ServerInformation = ServerInformation,
        OAuthSession = OAuthSession
    };

    public static ImapCalDavSetupResult FromAccountSetupData(ImapAccountSetupData data) => new()
    {
        DisplayName = data.DisplayName,
        EmailAddress = data.EmailAddress,
        IsMailAccessGranted = data.IsMailAccessGranted,
        IsCalendarAccessGranted = data.IsCalendarAccessGranted,
        ShouldAppendMessagesToSentFolder = data.ShouldAppendMessagesToSentFolder,
        ServerInformation = data.ServerInformation,
        OAuthSession = data.OAuthSession
    };
}

public sealed class ImapCalendarSupportModeOption(ImapCalendarSupportMode mode, string title)
{
    public ImapCalendarSupportMode Mode { get; } = mode;
    public string Title { get; } = title;
}
