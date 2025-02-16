using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class SyncExistenceHandler : ServerMessageHandler<SynchronizationExistenceCheckRequest, bool>
    {
        public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

        private readonly ISynchronizerFactory _synchronizerFactory;

        public SyncExistenceHandler(ISynchronizerFactory synchronizerFactory)
        {
            _synchronizerFactory = synchronizerFactory;
        }

        protected override async Task<WinoServerResponse<bool>> HandleAsync(SynchronizationExistenceCheckRequest message, CancellationToken cancellationToken = default)
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(message.AccountId);

            return WinoServerResponse<bool>.CreateSuccessResponse(synchronizer.State != Wino.Core.Domain.Enums.AccountSynchronizerState.Idle);
        }
    }
}
