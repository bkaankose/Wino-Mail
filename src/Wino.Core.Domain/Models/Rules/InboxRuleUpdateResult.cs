using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Rules;

/// <summary>
/// Outcome of writing inbox-rule changes to the server. On failure, <see cref="Errors"/> carries
/// friendly per-operation messages (translated from the provider's error).
/// </summary>
public sealed class InboxRuleUpdateResult
{
    public bool Success { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = new List<string>();

    /// <summary>
    /// The server refused the update because classic Outlook's legacy rule blob exists on the mailbox
    /// (EWS ErrorOutlookRuleBlobExists, or the IPM.RuleOrganizer message over MAPI). Retrying with
    /// removeOutlookRuleBlob=true clears it: server rules are kept, but Outlook client-only rules are
    /// lost, so callers must get user consent first.
    /// </summary>
    public bool RequiresOutlookRuleBlobRemoval { get; init; }

    public static InboxRuleUpdateResult Ok { get; } = new() { Success = true };

    public static InboxRuleUpdateResult Failed(params string[] errors) =>
        new() { Success = false, Errors = errors?.Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? new List<string>() };

    public static InboxRuleUpdateResult Failed(IEnumerable<string> errors) =>
        new() { Success = false, Errors = errors?.Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? new List<string>() };
}
