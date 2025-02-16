using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers;

public class TerminateServerRequestHandler : ServerMessageHandler<TerminateServerRequested, bool>
{
    public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex) => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

    protected override Task<WinoServerResponse<bool>> HandleAsync(TerminateServerRequested message, CancellationToken cancellationToken = default)
    {
        // This handler is only doing the logging right now.
        // Client will always expect success response.
        // Server will be terminated in the server context once the client gets the response.

        Log.Information("Terminate server is requested by client. Killing server.");

        return Task.FromResult(WinoServerResponse<bool>.CreateSuccessResponse(true));
    }
}
