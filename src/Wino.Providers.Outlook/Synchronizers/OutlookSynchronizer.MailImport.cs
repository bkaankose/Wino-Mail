using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Outlook.Synchronizers;

/// <summary>
/// Graph has no way to store a message as received mail: a message created from MIME is a draft. Moved mail therefore
/// goes in over Exchange Online IMAP with APPEND, which keeps it an exact received copy. The IMAP token is a separate
/// opt-in permission (<see cref="ProviderFeature.CrossAccountMoveImport"/>).
/// </summary>
public partial class OutlookSynchronizer
{
    private const string ExchangeOnlineImapHost = "outlook.office365.com";
    private const int ExchangeOnlineImapPort = 993;

    private readonly IMailImportTokenProvider _mailImportTokenProvider;

    public override bool SupportsMailImport => _mailImportTokenProvider is not null;

    public override async Task<MailImportResult> ImportMailAsync(MailImportPackage package, CancellationToken cancellationToken = default)
    {
        if (_mailImportTokenProvider is null)
            throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, GetType()));

        var token = await _mailImportTokenProvider.GetMailImportTokenAsync(Account, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            throw new MailImportUnavailableException(MailImportUnavailableReason.AuthorizationRequired, "The account has not granted Exchange Online IMAP access.");

        var userName = string.IsNullOrWhiteSpace(Account.AuthenticationAddress) ? Account.Address : Account.AuthenticationAddress;

        using var client = new ImapClient { Timeout = (int)TimeSpan.FromMinutes(2).TotalMilliseconds };

        await client.ConnectAsync(ExchangeOnlineImapHost, ExchangeOnlineImapPort, SecureSocketOptions.SslOnConnect, cancellationToken).ConfigureAwait(false);

        try
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(userName, token), cancellationToken).ConfigureAwait(false);
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            // Exchange Online answers "AUTHENTICATE failed" both for a mailbox with IMAP turned off and for a token
            // it does not accept; neither is fixed by trying the next message.
            throw new MailImportUnavailableException(MailImportUnavailableReason.SignInRejected, ex.Message, ex);
        }

        try
        {
            var folder = await ResolveImportFolderAsync(client, package.TargetFolder, cancellationToken).ConfigureAwait(false);
            return await ImapMailImport.AppendAsync(folder, client.AppendLimit, package, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the IMAP folder of a Graph folder. Well-known folders go by role (Inbox, then RFC 6154 special use);
    /// any other folder goes by its display path from the nearest root, because Exchange names IMAP folders after
    /// their display names. A rename or a duplicate sibling name that the local folder list has not caught up with yet
    /// fails the lookup, and the mail stays in its source.
    /// </summary>
    private async Task<IMailFolder> ResolveImportFolderAsync(ImapClient client, MailItemFolder target, CancellationToken cancellationToken)
    {
        var localFolders = await _outlookChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var foldersByRemoteId = localFolders
            .Where(folder => !string.IsNullOrEmpty(folder.RemoteFolderId))
            .GroupBy(folder => folder.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var chain = new List<MailItemFolder> { target };
        var visited = new HashSet<string>(StringComparer.Ordinal) { target.RemoteFolderId ?? string.Empty };
        var current = target;

        while (GetRoleFolder(client, current) is null
               && !string.IsNullOrEmpty(current.ParentRemoteFolderId)
               && foldersByRemoteId.TryGetValue(current.ParentRemoteFolderId, out var parent)
               && visited.Add(parent.RemoteFolderId))
        {
            chain.Insert(0, parent);
            current = parent;
        }

        var root = chain[0];
        var folder = GetRoleFolder(client, root);

        if (folder is null)
        {
            var personal = client.PersonalNamespaces.FirstOrDefault()
                ?? throw new InvalidOperationException("The IMAP server reports no personal namespace.");

            folder = await client.GetFolderAsync(
                string.IsNullOrEmpty(personal.Path) ? root.FolderName : personal.Path + personal.DirectorySeparator + root.FolderName,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var child in chain.Skip(1))
            folder = await folder.GetSubfolderAsync(child.FolderName, cancellationToken).ConfigureAwait(false);

        return folder;
    }

    private static IMailFolder GetRoleFolder(ImapClient client, MailItemFolder folder)
    {
        if (folder.SpecialFolderType == SpecialFolderType.Inbox)
            return client.Inbox;

        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) == 0)
            return null;

        SpecialFolder? role = folder.SpecialFolderType switch
        {
            SpecialFolderType.Sent => SpecialFolder.Sent,
            SpecialFolderType.Deleted => SpecialFolder.Trash,
            SpecialFolderType.Junk => SpecialFolder.Junk,
            SpecialFolderType.Archive => SpecialFolder.Archive,
            _ => null
        };

        return role is { } specialFolder ? client.GetFolder(specialFolder) : null;
    }
}
