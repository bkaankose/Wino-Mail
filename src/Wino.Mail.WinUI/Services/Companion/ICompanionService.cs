using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.WinUI.Services.Companion;

internal enum CompanionReadinessState
{
    Initializing,
    NoAccounts,
    Ready
}

internal interface ICompanionService
{
    event EventHandler<string>? SessionDisabled;

    bool IsAvailableForSession { get; }

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task SetReadinessAsync(CompanionReadinessState state, CancellationToken cancellationToken = default);
    void PrepareForTrayInteraction();
    Task ToggleAsync(CancellationToken cancellationToken = default);
    void Hide();
    void RepositionIfOpen();
    Task ShutdownAsync();
}
