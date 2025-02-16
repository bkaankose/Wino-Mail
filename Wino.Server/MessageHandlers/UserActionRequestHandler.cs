using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers;

public class UserActionRequestHandler : ServerMessageHandler<ServerRequestPackage, bool>
{
    private readonly ISynchronizerFactory _synchronizerFactory;

    public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex) => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

    public UserActionRequestHandler(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    protected override async Task<WinoServerResponse<bool>> HandleAsync(ServerRequestPackage package, CancellationToken cancellationToken = default)
    {
        var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(package.AccountId);
        synchronizer.QueueRequest(package.Request);

        //if (package.QueueSynchronization)
        //{
        //    var options = new SynchronizationOptions
        //    {
        //        AccountId = package.AccountId,
        //        Type = Wino.Core.Domain.Enums.SynchronizationType.ExecuteRequests
        //    };

        //    WeakReferenceMessenger.Default.Send(new NewSynchronizationRequested(options));
        //}

        return WinoServerResponse<bool>.CreateSuccessResponse(true);
    }
}
