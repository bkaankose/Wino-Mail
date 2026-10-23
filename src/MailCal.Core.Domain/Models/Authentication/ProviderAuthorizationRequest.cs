using System.Collections.Generic;
using System.Linq;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Authentication;

public sealed record ProviderAuthorizationRequest(
    bool IncludeMail,
        bool IncludeCalendar,
        IReadOnlyCollection<ProviderFeature> Features,
        bool IncludeContacts = false,
        bool IncludeTasks = false)
{
    public static ProviderAuthorizationRequest ForAccount(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> features = null)
        => new(
            account?.IsMailAccessGranted != false,
            account?.IsCalendarAccessGranted == true,
              features?.Distinct().ToArray() ?? [],
              account?.IsContactAccessGranted == true,
              account?.IsTaskAccessGranted == true);
}
