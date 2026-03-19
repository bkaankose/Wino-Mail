using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public class WinoAccountAddOnCache
{
    [PrimaryKey]
    public Guid AccountId { get; set; }

    public bool HasAiPack { get; set; }

    public int? AiUsageCount { get; set; }

    public int? AiUsageLimit { get; set; }

    public DateTime? AiBillingPeriodStartUtc { get; set; }

    public DateTime? AiBillingPeriodEndUtc { get; set; }

    public bool HasUnlimitedAccounts { get; set; }

    public DateTime LastUpdatedUtc { get; set; }
}
