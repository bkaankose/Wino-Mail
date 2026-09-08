using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Gmail.v1.Data;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Synchronizers.Mail;

public partial class GmailSynchronizer
{
    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot,
        MailCopy draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(draft.DraftId)) throw new InvalidOperationException("Draft identity is unavailable.");
        using var mime = snapshot.OpenMime();
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        var raw = Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var body = new Draft { Message = new Message { Raw = raw, ThreadId = draft.ThreadId } };

        try
        {
            var updated = await _gmailService.Users.Drafts.Update(body, "me", draft.DraftId)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new(updated.Message.Id, updated.Id, updated.Message.ThreadId);
        }
        catch
        {
            // Cancellation/transport loss can happen after Gmail replaced the message.
            // Resolve the stable draft container before send/discard or the next save uses its ID.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var current = await _gmailService.Users.Drafts.Get("me", draft.DraftId)
                    .ExecuteAsync(recovery.Token).ConfigureAwait(false);
                await _gmailChangeProcessor.UpdateDraftIdentityAsync(Account.Id, snapshot.UniqueId,
                    new(current.Message.Id, current.Id, current.Message.ThreadId)).ConfigureAwait(false);
            }
            catch (Exception) { /* The coordinator logs only safe identifiers for the original failure. */ }
            throw;
        }
    }
}
