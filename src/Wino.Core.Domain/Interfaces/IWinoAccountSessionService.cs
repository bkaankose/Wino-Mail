#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <param name="IsInvalid">The API refused the refresh token for good; nothing may call the API with this session.</param>
public sealed record WinoAccountSession(Guid AccountId, long Generation, CancellationToken CancellationToken, bool IsInvalid = false);

public interface IWinoAccountSessionService
{
    Task<WinoAccountSession?> CaptureAsync(CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(WinoAccountSession session, CancellationToken cancellationToken = default);
    Task<bool> CommitAsync(WinoAccountSession session, Func<Task> commit, CancellationToken cancellationToken = default);
    Task ReplaceAsync(WinoAccount? account, Func<Task> beforeReplace, CancellationToken cancellationToken = default);
    Task<WinoAccount?> RefreshCredentialsAsync(WinoAccountSession session, string? rejectedAccessToken,
        Func<WinoAccount, CancellationToken, Task<WinoAccount?>> refresh, CancellationToken cancellationToken = default);

    /// <summary>Records that the API refused the session's refresh token for good. Returns false when the session is no longer current.</summary>
    Task<bool> MarkInvalidAsync(WinoAccountSession session, string reason, CancellationToken cancellationToken = default);
}
