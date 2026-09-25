using System;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Integration.Processors;

namespace Wino.Core.Synchronizers;

/// <summary>
/// Once a send has gone out, the draft it came from has to go too - and which copy that means
/// depends on whether the draft ever reached the server.
///
/// A draft that did reach it is returned as it is, for the provider to delete. One that never did
/// has no server copy for a sync to notice is gone, so its local row would otherwise stay in Drafts
/// for good; it is discarded here instead. That discard runs under the same lock as the mapping a
/// server create does, which settles the race either way: a create that finishes afterwards finds
/// nothing to map and deletes what it made, and one that finished first leaves a server copy, which
/// is returned for the provider to delete like any other.
///
/// Never throws. Everything after a send is tidying, and a send reported as failed after it went out
/// brings the draft back - which is how a recipient ends up with the message twice.
/// </summary>
internal static class SentDraftCleanup
{
    /// <returns>The server-side draft to delete, or null when there is none.</returns>
    public static async Task<MailCopy> ResolveAsync(IDefaultChangeProcessor changeProcessor, Guid accountId, MailCopy sentDraft)
    {
        if (sentDraft is null)
            return null;

        if (!sentDraft.IsLocalDraft)
            return sentDraft;

        try
        {
            return await changeProcessor.DiscardLocalDraftAsync(accountId, sentDraft.UniqueId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "A draft was sent but its local copy could not be removed; it may still show in Drafts.");
            return null;
        }
    }
}
