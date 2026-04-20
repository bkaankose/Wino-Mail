using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

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
        AccountCreationDialogResult accountCreationDialogResult)
        => new()
        {
            Mode = ImapCalDavSettingsPageMode.Wizard,
            AccountCreationDialogResult = accountCreationDialogResult
        };

    public static ImapCalDavSettingsNavigationContext CreateForAddAccountMode(
        AccountCreationDialogResult accountCreationDialogResult)
        => new()
        {
            Mode = ImapCalDavSettingsPageMode.AddAccount,
            AccountCreationDialogResult = accountCreationDialogResult
        };

    public bool IsWizardMode => Mode == ImapCalDavSettingsPageMode.Wizard;
}

public sealed class ImapCalDavSetupResult
{
    public string DisplayName { get; init; }
    public string EmailAddress { get; init; }
    public bool IsMailAccessGranted { get; init; }
    public bool IsCalendarAccessGranted { get; init; }
    public CustomServerInformation ServerInformation { get; init; }
}

public sealed class ImapCalendarSupportModeOption(ImapCalendarSupportMode mode, string title)
{
    public ImapCalendarSupportMode Mode { get; } = mode;
    public string Title { get; } = title;
}
