using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class SingleMimeDownloadHandler : ServerMessageHandler<DownloadMissingMessageRequested, bool>
    {
        public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex) => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

        private readonly ISynchronizerFactory _synchronizerFactory;
        public SingleMimeDownloadHandler(ISynchronizerFactory synchronizerFactory)
        {
            _synchronizerFactory = synchronizerFactory;
        }

        protected override async Task<WinoServerResponse<bool>> HandleAsync(DownloadMissingMessageRequested message, CancellationToken cancellationToken = default)
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(message.AccountId);

            // TODO: ITransferProgress support is lost.
            await synchronizer.DownloadMissingMimeMessageAsync(message.MailItem, null, cancellationToken);

            return WinoServerResponse<bool>.CreateSuccessResponse(true);
        }
    }
}
