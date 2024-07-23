using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    /// <summary>
    /// Handler for NewSynchronizationRequested from the client.
    /// </summary>
    public class SynchronizationRequestHandler : ServerMessageHandler<NewSynchronizationRequested, SynchronizationResult>
    {
        public override SynchronizationResult FailureDefaultResponse(Exception ex) => SynchronizationResult.Failed(ex);

        private readonly ISynchronizerFactory _synchronizerFactory;
        public SynchronizationRequestHandler(ISynchronizerFactory synchronizerFactory)
        {
            _synchronizerFactory = synchronizerFactory;
        }

        protected override async Task<SynchronizationResult> HandleAsync(NewSynchronizationRequested message, CancellationToken cancellationToken = default)
        {
            var synchronizer = _synchronizerFactory.GetAccountSynchronizer(message.Options.AccountId);

            return await synchronizer.SynchronizeAsync(message.Options, cancellationToken).ConfigureAwait(false);
        }
    }
}
