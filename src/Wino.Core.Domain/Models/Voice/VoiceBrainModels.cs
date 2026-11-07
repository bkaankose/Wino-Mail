#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>The brain as the server returned it, or the copy cached at the last successful download.</summary>
public sealed record VoiceBrainSnapshot(VoiceBrainDto Brain, bool IsCached);

/// <summary>
/// One open question of a plan. <see cref="Phrase"/> is the user's words that the answer replaces;
/// <see cref="Text"/> is the question as the bubble asks it.
/// </summary>
public sealed record VoiceQuestion(UnresolvedKind Kind, string Phrase, bool IsRelationship, string Text)
{
    /// <summary>People and folders can be many, so their pickers have a filter box.</summary>
    public bool IsSearchable => Kind is UnresolvedKind.Person or UnresolvedKind.Folder;
}

/// <summary>
/// Something the user can pick to answer a question: a person (<see cref="Address"/>), an account, a folder or a
/// calendar. <see cref="GroupTitle"/> names the account a folder or calendar belongs to.
/// </summary>
public sealed record VoiceAnswerCandidate(
    string Key,
    string Title,
    string? Detail,
    string? GroupTitle,
    MailAccount? Account,
    MailItemFolder? Folder = null,
    AccountCalendar? Calendar = null,
    string? Address = null);

public static class VoiceProviderKinds
{
    public static MailProviderKind ToProviderKind(MailProviderType type) => type switch
    {
        MailProviderType.Outlook => MailProviderKind.Outlook,
        MailProviderType.Gmail => MailProviderKind.Gmail,
        MailProviderType.POP3 => MailProviderKind.POP3,
        _ => MailProviderKind.IMAP4
    };

    /// <summary>The account's key in the brain, or null for an account without an address.</summary>
    public static BrainMailboxKey? ToMailboxKey(MailAccount account)
        => string.IsNullOrWhiteSpace(account.Address)
            ? null
            : new BrainMailboxKey(BrainMailboxKey.Normalize(account.Address), ToProviderKind(account.ProviderType));
}

/// <summary>A plan with the questions the device answered itself applied, and the facts those answers become.</summary>
public sealed record VoiceKnownAnswers(VoicePlan Plan, IReadOnlyList<AddBrainFactRequest> Facts);
