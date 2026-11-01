#nullable enable
using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Core.Domain.Models.Accounts;

public enum AccountSetupKind
{
    /// <summary>Gmail or Outlook through their APIs.</summary>
    Native,

    /// <summary>A catalog provider set up with the password typed for it (<see cref="AccountSetupPlan.SpecialImap"/>).</summary>
    SpecialImap,

    /// <summary>Servers validated by the caller (<see cref="AccountSetupPlan.Imap"/>), including local-only accounts.</summary>
    Imap,

    /// <summary>An on-premises Exchange account the caller already checked (<see cref="AccountSetupPlan.Exchange"/>).</summary>
    Exchange
}

/// <summary>
/// The IMAP-family account the caller already validated: servers, credentials and, for browser sign-in, the in-memory
/// OAuth session whose key becomes the account id.
/// </summary>
public sealed class ImapAccountSetupData
{
    public string? DisplayName { get; init; }
    public string? EmailAddress { get; init; }
    public bool IsMailAccessGranted { get; init; }
    public bool IsCalendarAccessGranted { get; init; }
    public bool ShouldAppendMessagesToSentFolder { get; init; } = true;
    public CustomServerInformation? ServerInformation { get; init; }
    public OAuthSetupSession? OAuthSession { get; init; }

    /// <summary>
    /// A local-only account still carries server information so the IMAP-family account model stays whole, but no
    /// endpoint is set and nothing is validated.
    /// </summary>
    public static ImapAccountSetupData CreateLocalOnly(string? accountName, ImapCalendarSupportMode calendarSupportMode) => new()
    {
        DisplayName = accountName,
        EmailAddress = string.Empty,
        IsMailAccessGranted = false,
        IsCalendarAccessGranted = calendarSupportMode != ImapCalendarSupportMode.Disabled,
        ShouldAppendMessagesToSentFolder = false,
        ServerInformation = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            IncomingServerType = CustomIncomingServerType.IMAP4,
            CalendarSupportMode = calendarSupportMode,
            MaxConcurrentClients = 5,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected
        }
    };
}

/// <summary>
/// An on-premises Exchange account the caller already checked: its server information with the password, or, for
/// modern auth, the OAuth settings and the id whose stored refresh token the interactive sign-in wrote.
/// </summary>
public sealed class ExchangeAccountSetupData
{
    public string? DisplayName { get; init; }
    public string? EmailAddress { get; init; }
    public CustomServerInformation? ServerInformation { get; init; }

    /// <summary>The id the Exchange sign-in stored the refresh token under; the saved account takes it.</summary>
    public Guid? SignedInAccountId { get; init; }
}

/// <summary>
/// A Gmail or Outlook sign-in that already happened against <see cref="TransientAccount"/>, an unsaved account built by
/// <see cref="Interfaces.IAccountSetupRunner.CreateTransientAccount"/>. Its id keys the stored token, so the saved
/// account keeps it.
/// </summary>
public sealed record NativeSignInResult(MailAccount TransientAccount, TokenInformationEx TokenInformation);

/// <summary>Everything <see cref="Interfaces.IAccountSetupRunner"/> needs to save and first-sync one account.</summary>
public sealed class AccountSetupPlan
{
    public AccountSetupKind Kind { get; init; }
    public MailProviderType ProviderType { get; init; }
    public SpecialImapProvider SpecialImapProvider { get; init; }

    /// <summary>The provider name in "Authenticating with {0}".</summary>
    public string ProviderName { get; init; } = string.Empty;

    public string? AccountName { get; init; }
    public string? AccountColorHex { get; init; }
    public InitialSynchronizationRange InitialSynchronizationRange { get; init; } = InitialSynchronizationRange.SixMonths;

    public bool IsMailAccessEnabled { get; init; } = true;
    public bool IsCalendarAccessEnabled { get; init; }
    public bool IsContactAccessEnabled { get; init; }
    public bool IsTaskAccessEnabled { get; init; }
    public AccountIntegrationSource CalendarIntegrationSource { get; init; } = AccountIntegrationSource.Local;
    public AccountIntegrationSource ContactIntegrationSource { get; init; } = AccountIntegrationSource.Local;
    public AccountIntegrationSource TaskIntegrationSource { get; init; } = AccountIntegrationSource.Local;

    /// <summary>An account must serve at least one of mail, calendar, contacts and To Do; a local account has no mail.</summary>
    public bool HasAnyCapability => IsMailAccessEnabled || IsCalendarAccessEnabled || IsContactAccessEnabled || IsTaskAccessEnabled;

    /// <summary>The typed address. For native routes it becomes the transient account's login hint.</summary>
    public string? EmailAddress { get; init; }

    /// <summary>
    /// For <see cref="AccountSetupKind.Native"/>: the sign-in the setup page already completed. Null runs the
    /// interactive sign-in as the first step.
    /// </summary>
    public NativeSignInResult? NativeSignIn { get; init; }

    /// <summary>For <see cref="AccountSetupKind.SpecialImap"/>: the provider, password, region and calendar mode.</summary>
    public AccountCreationDialogResult? SpecialImap { get; init; }

    /// <summary>For <see cref="AccountSetupKind.SpecialImap"/>: the sender name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>For <see cref="AccountSetupKind.Imap"/>.</summary>
    public ImapAccountSetupData? Imap { get; init; }

    /// <summary>For <see cref="AccountSetupKind.Exchange"/>.</summary>
    public ExchangeAccountSetupData? Exchange { get; init; }

    /// <summary>The calendar mode the special IMAP steps are listed for (CalDAV adds the discovery and test steps).</summary>
    public ImapCalendarSupportMode CalendarSupportMode { get; init; } = ImapCalendarSupportMode.Disabled;
}

public enum AccountSetupStepKind
{
    Authenticating,
    TestingMailAuth,
    DiscoveringCalDav,
    TestingCalendarAuth,
    SavingAccount,
    FetchingProfile,
    SyncingFolders,
    SyncingCategories,
    SyncingAliases,
    FetchingCalendarMetadata,
    Finalizing
}

public sealed record AccountSetupStepDescriptor(AccountSetupStepKind Kind, string Title);

public sealed record AccountSetupStepUpdate(AccountSetupStepKind Kind, AccountSetupStepStatus Status, string? ErrorMessage = null);

public sealed class AccountSetupRunResult
{
    /// <summary>The account, whether or not setup finished. Null when nothing was built.</summary>
    public MailAccount? Account { get; init; }

    /// <summary>The account row exists; a failure after this keeps it, and Try again enters the shell.</summary>
    public bool IsAccountSaved { get; init; }

    public bool IsCompleted { get; init; }

    /// <summary>The user cancelled the interactive sign-in; the caller goes back without an error.</summary>
    public bool IsCanceled { get; init; }

    public Exception? Error { get; init; }
}
