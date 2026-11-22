using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Services;
using Wino.Services.Models.MailItem;
using Wino.Providers.Gmail.Google.Apis.Gmail.v1.Data;

namespace Wino.Providers.Gmail.Synchronizers;

public partial class GmailSynchronizer
{
    public override bool SupportsMailImport => true;

    public override async Task<MailImportResult> ImportMailAsync(MailImportPackage package, CancellationToken cancellationToken = default)
    {
        // Archive is not a label: a message without INBOX and without a user label is archived.
        var labelIds = new List<string>();
        var targetLabelId = package.TargetFolder.RemoteFolderId;
        if (!string.IsNullOrWhiteSpace(targetLabelId) && targetLabelId != ServiceConstants.ARCHIVE_LABEL_ID)
            labelIds.Add(targetLabelId);
        if (!package.IsRead)
            labelIds.Add(ServiceConstants.UNREAD_LABEL_ID);
        if (package.IsFlagged)
            labelIds.Add(ServiceConstants.STARRED_LABEL_ID);

        using var stream = new MemoryStream();
        await package.Mime.WriteToAsync(FormatOptions.Default, stream, cancellationToken).ConfigureAwait(false);

        var inserted = await _gmailService.Users.Messages
            .Insert(new Message { LabelIds = labelIds }, stream.ToArray(), "me")
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MailImportResult(inserted?.Id);
    }
}
