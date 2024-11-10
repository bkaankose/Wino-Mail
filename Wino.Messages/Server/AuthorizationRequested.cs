using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    /// <summary>
    /// For delegating authentication/authorization to the server app.
    /// </summary>
    public record AuthorizationRequested(MailProviderType MailProviderType, MailAccount CreatedAccount, bool ProposeCopyAuthorizationURL) : IClientMessage;
}
