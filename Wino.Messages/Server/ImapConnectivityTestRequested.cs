using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    public record ImapConnectivityTestRequested(CustomServerInformation ServerInformation, bool IsSSLHandshakeAllowed) : IClientMessage;
}
