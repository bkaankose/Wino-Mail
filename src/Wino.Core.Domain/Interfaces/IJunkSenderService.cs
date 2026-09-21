using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Manages the per-account safe/blocked sender lists (the source of truth for the Junk email
/// settings page, import/export, and the Block / Never block actions).
/// </summary>
public interface IJunkSenderService
{
    Task<IReadOnlyList<JunkSender>> GetSendersAsync(Guid accountId, JunkListType listType);

    /// <summary>Adds an address to a list, removing it from the opposite list (a sender is on one list at a time).</summary>
    Task AddSenderAsync(Guid accountId, string address, JunkListType listType);

    Task RemoveSenderAsync(Guid accountId, string address, JunkListType listType);

    Task<bool> IsListedAsync(Guid accountId, string address, JunkListType listType);

    /// <summary>Bulk add (import). Returns how many entries were newly added.</summary>
    Task<int> ImportAsync(Guid accountId, JunkListType listType, IEnumerable<string> addresses);
}
