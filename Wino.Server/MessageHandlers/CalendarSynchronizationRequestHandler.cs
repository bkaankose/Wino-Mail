using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers;

public class CalendarSynchronizationRequestHandler : ServerMessageHandler<NewCalendarSynchronizationRequested, CalendarSynchronizationResult>
{
    public override WinoServerResponse<CalendarSynchronizationResult> FailureDefaultResponse(Exception ex)
       => WinoServerResponse<CalendarSynchronizationResult>.CreateErrorResponse(ex.Message);

    private readonly ISynchronizerFactory _synchronizerFactory;

    public CalendarSynchronizationRequestHandler(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    protected override async Task<WinoServerResponse<CalendarSynchronizationResult>> HandleAsync(NewCalendarSynchronizationRequested message, CancellationToken cancellationToken = default)
    {
        var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(message.Options.AccountId);

        try
        {
            var synchronizationResult = await synchronizer.SynchronizeCalendarEventsAsync(message.Options, cancellationToken);

            return WinoServerResponse<CalendarSynchronizationResult>.CreateSuccessResponse(synchronizationResult);
        }
        catch (Exception ex)
        {
            throw;
        }
    }
}
