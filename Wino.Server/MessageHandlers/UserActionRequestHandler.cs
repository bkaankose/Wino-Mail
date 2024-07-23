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

        protected override Task<bool> HandleAsync(ServerRequestPackage package, CancellationToken cancellationToken = default)
        {
            var synchronizer = _synchronizerFactory.GetAccountSynchronizer(package.AccountId);

            synchronizer.QueueRequest(package.Request);

            return Task.FromResult(true);
        }
    }
}
