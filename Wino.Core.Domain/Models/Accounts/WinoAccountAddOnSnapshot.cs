using System;

namespace Wino.Core.Domain.Models.Accounts;

public sealed record WinoAccountAddOnSnapshot(
    bool HasAiPack,
    int? UsageCount = null,
    int? UsageLimit = null,
    DateTimeOffset? BillingPeriodStartUtc = null,
    DateTimeOffset? BillingPeriodEndUtc = null,
    bool HasUnlimitedAccounts = false,
    DateTimeOffset? LastUpdatedUtc = null);
