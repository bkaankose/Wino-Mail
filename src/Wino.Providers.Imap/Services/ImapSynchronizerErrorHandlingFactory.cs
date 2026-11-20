using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Services;
using Wino.Providers.Imap.Interfaces;
using Wino.Providers.Imap.Synchronizers.Errors.Imap;

namespace Wino.Providers.Imap.Services;

/// <summary>
/// Factory for handling IMAP synchronizer errors.
/// Registers and routes errors to appropriate handlers.
/// </summary>
public class ImapSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IImapSynchronizerErrorHandlerFactory
{
    public ImapSynchronizerErrorHandlingFactory(
        ImapConnectionLostHandler connectionLostHandler,
        ImapAuthenticationFailedHandler authFailedHandler,
        EntityNotFoundHandler entityNotFoundHandler,
        ImapFolderNotFoundHandler folderNotFoundHandler,
        ImapProtocolErrorHandler protocolErrorHandler)
    {
        // Order matters - more specific handlers should be registered first
        RegisterHandler(authFailedHandler);
        RegisterHandler(entityNotFoundHandler);
        RegisterHandler(folderNotFoundHandler);
        RegisterHandler(connectionLostHandler);
        RegisterHandler(protocolErrorHandler); // Most generic, registered last
    }
}
