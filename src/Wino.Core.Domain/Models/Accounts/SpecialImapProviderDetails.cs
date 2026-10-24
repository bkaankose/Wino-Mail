using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <param name="RegionId">
/// The catalog region whose hosts the account uses. Null or empty selects the provider's first region.
/// Ignored for providers without regions.
/// </param>
/// <param name="LoginAddress">
/// The provider address that signs in when <paramref name="Address"/> is on a custom domain, such as
/// the iCloud Mail address behind an iCloud+ custom domain. Null or empty signs in with <paramref name="Address"/>.
/// </param>
public record SpecialImapProviderDetails(
    string Address,
    string Password,
    string SenderName,
    SpecialImapProvider SpecialImapProvider,
    ImapCalendarSupportMode CalendarSupportMode = ImapCalendarSupportMode.CalDav,
    string RegionId = null,
    string LoginAddress = null);
