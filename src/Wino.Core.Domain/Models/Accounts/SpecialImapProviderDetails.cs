using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <param name="RegionId">
/// The catalog region whose hosts the account uses. Null or empty selects the provider's first region.
/// Ignored for providers without regions.
/// </param>
public record SpecialImapProviderDetails(
    string Address,
    string Password,
    string SenderName,
    SpecialImapProvider SpecialImapProvider,
    ImapCalendarSupportMode CalendarSupportMode = ImapCalendarSupportMode.CalDav,
    string RegionId = null);
