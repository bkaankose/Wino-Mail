using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MimeKit;
using MimeKit.IO;
using Wino.Core.Domain.Extensions;
using Wino.Services.Models.MailItem;

namespace Wino.Core.Integration;

/// <summary>
/// Stores a moved message with IMAP APPEND (RFC 3501 6.3.11), the way Thunderbird copies mail between accounts: the
/// exact MIME, its read and flagged state and its original received time. IMAP and the Outlook target share it.
/// </summary>
public static class ImapMailImport
{
    public static async Task<MailImportResult> AppendAsync(IMailFolder folder, uint? appendLimit, MailImportPackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(package);

        // \Draft is never set: the copy is received mail, not a draft of the target account.
        var flags = MessageFlags.None;
        if (package.IsRead) flags |= MessageFlags.Seen;
        if (package.IsFlagged) flags |= MessageFlags.Flagged;

        // RFC 7889: a server that advertises APPENDLIMIT refuses larger messages; failing early keeps the mail in place.
        if (appendLimit is > 0)
        {
            using var measuring = new MeasuringStream();
            await package.Mime.WriteToAsync(FormatOptions.Default, measuring, cancellationToken).ConfigureAwait(false);

            if (measuring.Length > appendLimit.Value)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The message is {0} bytes; the server accepts at most {1}.", measuring.Length, appendLimit.Value));
        }

        var request = new AppendRequest(package.Mime, flags)
        {
            InternalDate = new DateTimeOffset(package.ReceivedDateUtc.AsStoredUtc())
        };

        var uid = await folder.AppendAsync(request, cancellationToken).ConfigureAwait(false);

        return new MailImportResult(uid?.Id.ToString(CultureInfo.InvariantCulture));
    }
}
