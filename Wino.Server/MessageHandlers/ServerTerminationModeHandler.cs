using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers;

public class ServerTerminationModeHandler : ServerMessageHandler<ServerTerminationModeChanged, bool>
{
    public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex) => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

    protected override Task<WinoServerResponse<bool>> HandleAsync(ServerTerminationModeChanged message, CancellationToken cancellationToken = default)
    {
        WeakReferenceMessenger.Default.Send(message);

        return Task.FromResult(WinoServerResponse<bool>.CreateSuccessResponse(true));
    }
}
