using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Serilog;

namespace Wino.Core.Extensions;

internal static class ImapMailFolderCloseExtensions
{
    public static async Task CloseSelectedMailboxAsync(
        this IImapClient client,
        IMailFolder folder,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await CloseSelectedMailboxAsync(client, folder, expunge: false, logger, cancellationToken).ConfigureAwait(false);
    }

    public static async Task CloseSelectedMailboxAsync(
        this IImapClient client,
        IMailFolder folder,
        bool expunge,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (folder?.IsOpen != true)
            return;

        try
        {
            await folder.CloseAsync(expunge, cancellationToken).ConfigureAwait(false);
        }
        catch (ImapCommandException ex) when (IsBenignCloseWithoutSelectedMailboxError(ex.Response, ex.Message))
        {
            logger?.Debug(ex, "IMAP UNSELECT failed because the server no longer has a selected mailbox for {FolderName}. Discarding the pooled client.", folder.FullName);
            await DisconnectStaleClientAsync(client, logger).ConfigureAwait(false);
        }
    }

    internal static bool IsBenignCloseWithoutSelectedMailboxError(ImapCommandResponse response, string message)
    {
        return response == ImapCommandResponse.Bad
            && message?.Contains("UNSELECT", StringComparison.OrdinalIgnoreCase) == true
            && message.Contains("select a mailbox first", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DisconnectStaleClientAsync(IImapClient client, ILogger logger)
    {
        if (client?.IsConnected != true)
            return;

        try
        {
            await client.DisconnectAsync(quit: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "Failed to disconnect stale IMAP client after UNSELECT state mismatch.");
        }
    }
}
