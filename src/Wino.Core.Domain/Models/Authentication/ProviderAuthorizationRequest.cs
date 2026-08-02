using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Authentication;

public sealed record ProviderAuthorizationRequest(
    bool IncludeMail,
    bool IncludeCalendar,
    IReadOnlyCollection<ProviderFeature> Features)
{
    public static ProviderAuthorizationRequest ForAccount(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> features = null)
        => new(
            account?.IsMailAccessGranted != false,
            account?.IsCalendarAccessGranted == true,
            features?.Distinct().ToArray() ?? []);
}
