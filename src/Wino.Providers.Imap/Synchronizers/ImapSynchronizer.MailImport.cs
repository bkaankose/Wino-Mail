using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Integration;
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Imap.Synchronizers;

public partial class ImapSynchronizer
{
    public override bool SupportsMailImport => true;

    public override async Task<MailImportResult> ImportMailAsync(MailImportPackage package, CancellationToken cancellationToken = default)
    {
        var client = await _clientPool.GetClientAsync(cancellationToken, purpose: "MailImport").ConfigureAwait(false);

        var faulted = true;
        try
        {
            // APPEND needs no selected mailbox, so the pooled connection keeps whatever state it had.
            var remoteFolder = await client.GetFolderAsync(package.TargetFolder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            var result = await ImapMailImport.AppendAsync(remoteFolder, client.AppendLimit, package, cancellationToken).ConfigureAwait(false);
            faulted = false;
            return result;
        }
        catch (Exception ex) when (ex is ImapCommandException or InvalidOperationException)
        {
            // The server refused this message; the connection itself is fine.
            faulted = false;
            throw;
        }
        finally
        {
            _clientPool.Release(client, destroyClient: faulted);
        }
    }
}
