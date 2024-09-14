using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class ImapConnectivityTestHandler : ServerMessageHandler<ImapConnectivityTestRequested, ImapConnectivityTestResults>
    {
        private readonly IImapTestService _imapTestService;

        public override WinoServerResponse<ImapConnectivityTestResults> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<ImapConnectivityTestResults>.CreateErrorResponse(ex.Message);

        public ImapConnectivityTestHandler(IImapTestService imapTestService)
        {
            _imapTestService = imapTestService;
        }

        protected override async Task<WinoServerResponse<ImapConnectivityTestResults>> HandleAsync(ImapConnectivityTestRequested message, CancellationToken cancellationToken = default)
        {
            try
            {
                await _imapTestService.TestImapConnectionAsync(message.ServerInformation, message.IsSSLHandshakeAllowed);

                return WinoServerResponse<ImapConnectivityTestResults>.CreateSuccessResponse(ImapConnectivityTestResults.Success());
            }
            catch (ImapTestSSLCertificateException sslTestException)
            {
                // User must confirm to continue ignoring the SSL certificate.
                return WinoServerResponse<ImapConnectivityTestResults>.CreateSuccessResponse(ImapConnectivityTestResults.CertificateUIRequired(sslTestException.Issuer, sslTestException.ExpirationDateString, sslTestException.ValidFromDateString));
            }
            catch (ImapClientPoolException clientPoolException)
            {
                // Connectivity failed with protocol log.
                return WinoServerResponse<ImapConnectivityTestResults>.CreateSuccessResponse(ImapConnectivityTestResults.Failure(clientPoolException, clientPoolException.ProtocolLog));
            }
            catch (Exception exception)
            {
                // Unknown error 
                return WinoServerResponse<ImapConnectivityTestResults>.CreateSuccessResponse(ImapConnectivityTestResults.Failure(exception, string.Empty));
            }
        }
    }
}
