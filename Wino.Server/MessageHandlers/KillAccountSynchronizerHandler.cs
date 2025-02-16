using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class KillAccountSynchronizerHandler : ServerMessageHandler<KillAccountSynchronizerRequested, bool>
    {
        private readonly ISynchronizerFactory _synchronizerFactory;

        public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

        public KillAccountSynchronizerHandler(ISynchronizerFactory synchronizerFactory)
        {
            _synchronizerFactory = synchronizerFactory;
        }

        protected override async Task<WinoServerResponse<bool>> HandleAsync(KillAccountSynchronizerRequested message, CancellationToken cancellationToken = default)
        {
            await _synchronizerFactory.DeleteSynchronizerAsync(message.AccountId);

            return WinoServerResponse<bool>.CreateSuccessResponse(true);
        }
    }
}
