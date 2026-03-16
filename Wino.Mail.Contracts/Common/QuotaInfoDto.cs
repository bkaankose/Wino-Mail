namespace Wino.Mail.Api.Contracts.Common;

public sealed record QuotaInfoDto(
    bool HasAiPack,
    string EntitlementStatus,
    DateTimeOffset? CurrentPeriodStartUtc,
    DateTimeOffset? CurrentPeriodEndUtc,
    int? MonthlyLimit,
    int? Used,
    int? Remaining);
