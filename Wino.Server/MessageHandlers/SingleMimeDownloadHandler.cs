using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class SingleMimeDownloadHandler : ServerMessageHandler<DownloadMissingMessageRequested, bool>
    {
        public override bool FailureDefaultResponse(Exception ex) => false;

        private readonly ISynchronizerFactory _synchronizerFactory;
        public SingleMimeDownloadHandler(ISynchronizerFactory synchronizerFactory)
        {
            _synchronizerFactory = synchronizerFactory;
        }

        protected override async Task<bool> HandleAsync(DownloadMissingMessageRequested message, CancellationToken cancellationToken = default)
        {
            var synchronizer = _synchronizerFactory.GetAccountSynchronizer(message.AccountId);

            // TODO: ITransferProgress support is lost.
            await synchronizer.DownloadMissingMimeMessageAsync(message.MailItem, null, cancellationToken);

            return true;
        }
    }
}
