using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public record AccountCreationDialogResult(
    MailProviderType ProviderType,
    string AccountName,
    SpecialImapProviderDetails SpecialImapProviderDetails,
    string AccountColorHex,
    InitialSynchronizationRange InitialSynchronizationRange,
    bool IsMailAccessGranted,
    bool IsCalendarAccessGranted,
    bool IsContactAccessGranted = false,
    bool IsTaskAccessGranted = false,
    ImapCalendarSupportMode CalendarSupportMode = ImapCalendarSupportMode.Disabled);
