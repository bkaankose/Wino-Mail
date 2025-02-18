using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers;

public class OnlineSearchRequestHandler : ServerMessageHandler<OnlineSearchRequested, OnlineSearchResult>
{
    private readonly ISynchronizerFactory _synchronizerFactory;

    public OnlineSearchRequestHandler(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public override WinoServerResponse<OnlineSearchResult> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<OnlineSearchResult>.CreateErrorResponse(ex.Message);

    protected override async Task<WinoServerResponse<OnlineSearchResult>> HandleAsync(OnlineSearchRequested message, CancellationToken cancellationToken = default)
    {
        List<IWinoSynchronizerBase> synchronizers = new();

        foreach (var accountId in message.AccountIds)
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(accountId);
            synchronizers.Add(synchronizer);
        }

        var tasks = synchronizers.Select(s => s.OnlineSearchAsync(message.QueryText, message.Folders, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks);

        // Flatten the results from all synchronizers into a single list
        var allResults = results.SelectMany(x => x).ToList();

        return WinoServerResponse<OnlineSearchResult>.CreateSuccessResponse(new OnlineSearchResult(allResults));
    }
}
