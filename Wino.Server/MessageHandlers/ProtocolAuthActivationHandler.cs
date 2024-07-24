using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Client.Authorization;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class ProtocolAuthActivationHandler : ServerMessageHandler<ProtocolAuthorizationCallbackReceived, bool>
    {
        public override WinoServerResponse<bool> FailureDefaultResponse(Exception ex) => WinoServerResponse<bool>.CreateErrorResponse(ex.Message);

        private readonly INativeAppService _nativeAppService;

        public ProtocolAuthActivationHandler(INativeAppService nativeAppService)
        {
            _nativeAppService = nativeAppService;
        }

        protected override Task<WinoServerResponse<bool>> HandleAsync(ProtocolAuthorizationCallbackReceived message, CancellationToken cancellationToken = default)
        {
            _nativeAppService.ContinueAuthorization(message.AuthorizationResponseUri);

            return Task.FromResult(WinoServerResponse<bool>.CreateSuccessResponse(true));
        }
    }
}
