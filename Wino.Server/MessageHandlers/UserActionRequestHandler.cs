using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class UserActionRequestHandler : ServerMessageHandler<ServerRequestPackage, bool>
    {
        private readonly ISynchronizerFactory _synchronizerFactory;

        public override bool FailureDefaultResponse(Exception ex) => false;

        public UserActionRequestHandler(ISynchronizerFactory synchronizerFactory)
        {
            _synchronizerFactory = synchronizerFactory;
        }

        protected override async Task<bool> HandleAsync(ServerRequestPackage package, CancellationToken cancellationToken = default)
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(package.AccountId);

            synchronizer.QueueRequest(package.Request);

            return true;
        }
    }
}
