#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public sealed record WinoAccountSession(Guid AccountId, long Generation, CancellationToken CancellationToken);

public interface IWinoAccountSessionService
{
    Task<WinoAccountSession?> CaptureAsync(CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(WinoAccountSession session, CancellationToken cancellationToken = default);
    Task<bool> CommitAsync(WinoAccountSession session, Func<Task> commit, CancellationToken cancellationToken = default);
    Task ReplaceAsync(WinoAccount? account, Func<Task> beforeReplace, CancellationToken cancellationToken = default);
    Task<WinoAccount?> RefreshCredentialsAsync(WinoAccountSession session, string? rejectedAccessToken,
        Func<WinoAccount, CancellationToken, Task<WinoAccount?>> refresh, CancellationToken cancellationToken = default);
}
