using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The addresses a mailbox has written to before, offered under the To box ahead of the address book.
///
/// An account's list is read once - on the first keystroke that needs it - and then answered from
/// memory, because a round trip per keystroke would be no use to anybody. Outlook reads the same list
/// at startup instead; doing it lazily costs one read behind the composer's own typing delay and
/// spends nothing on an account whose composer is never opened. Writing is where this differs from
/// Outlook more sharply: it saves only when it closes, so a crash costs everything it learned that
/// session, whereas a send here is recorded straight away.
///
/// Providers that keep no such list contribute nothing, and the composer falls back to contacts and
/// the directory exactly as before.
/// </summary>
public interface IRememberedRecipientService
{
    /// <summary>
    /// The remembered addresses matching what has been typed, best first. Empty for an account whose
    /// provider keeps no list.
    /// </summary>
    Task<IReadOnlyList<RememberedRecipient>> SuggestAsync(Guid accountId, string query, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>Notes that a message went to these addresses, and saves that.</summary>
    Task RecordAsync(Guid accountId, IReadOnlyList<RememberedRecipient> recipients, CancellationToken cancellationToken = default);
}
