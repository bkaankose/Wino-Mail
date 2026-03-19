using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public sealed record WinoAddOnInfo(
    WinoAddOnProductType ProductType,
    bool IsPurchased,
    int? UsageCount = null,
    int? UsageLimit = null,
    double UsagePercentage = 0,
    DateTimeOffset? RenewalDateUtc = null);
