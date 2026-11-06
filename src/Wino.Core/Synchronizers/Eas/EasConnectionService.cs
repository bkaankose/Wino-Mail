using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Synchronizers.Eas;

public sealed class EasConnectionService : IEasConnectionService
{
    public async Task ValidateAsync(CustomServerInformation settings, Guid accountId, CancellationToken cancellationToken)
    {
        using var client = new EasClient(settings.IncomingServer, settings.IncomingServerUsername,
            settings.IncomingServerPassword ?? throw new InvalidOperationException("The EAS password is missing."), accountId);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await client.FolderSyncAsync("0", cancellationToken).ConfigureAwait(false);
    }
}
