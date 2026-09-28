#nullable enable
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Models.Accounts;

public enum WinoPurchaseRefreshOutcome
{
    Refreshed,
    Pending,
    SignInRequired,
    Failed
}

public sealed record WinoPurchaseRefreshResult(
    WinoPurchaseRefreshOutcome Outcome,
    WinoAccount? Account = null,
    WinoAccountIntelligenceSnapshot? Snapshot = null);
