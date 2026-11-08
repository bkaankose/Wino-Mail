#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// The mails one step acts on in one account: the mail list query that finds them and how many it found when the
/// plan was shown. The mails themselves are read again when the step runs, so the result reflects any sync since.
/// </summary>
/// <param name="Limit">At most this many mails, or 0 for every match.</param>
/// <param name="OldestFirst">Take the oldest matches instead of the newest.</param>
/// <param name="WithoutAttachments">Keep only mails without attachments; the query can't express it.</param>
/// <param name="NotFlagged">Keep only mails that aren't flagged; the query can't express it.</param>
/// <param name="SenderKind">Keep only mails from that kind of sender, as <see cref="MailSenderClassifier"/> decides.</param>
public sealed record VoiceMailTarget(
    MailAccount Account,
    MailListInitializationOptions Query,
    int Limit,
    bool OldestFirst,
    bool WithoutAttachments,
    bool NotFlagged,
    int Count,
    MailSenderKind SenderKind = MailSenderKind.Any);

/// <summary>
/// One calendar event a step acts on. <see cref="IsOrganizer"/>: the account organizes it, so deleting it cancels it
/// for <see cref="AttendeeCount"/> other people and there is no invitation to answer.
/// </summary>
public sealed record VoiceEventTarget(CalendarItem Event, MailAccount Account, bool IsOrganizer, int AttendeeCount);

/// <summary>One task a step acts on, with the list it is in.</summary>
public sealed record VoiceTaskTarget(AccountTask Task, AccountTaskList? List, MailAccount Account);

/// <summary>
/// A task list a step acts on. <see cref="List"/> is null for a list the step creates in <see cref="Account"/>.
/// </summary>
public sealed record VoiceTaskListTarget(AccountTaskList? List, MailAccount Account);

/// <summary>One contact a step acts on; <see cref="Account"/> is null for a contact of no local account.</summary>
public sealed record VoiceContactTarget(AccountContact Contact, MailAccount? Account);

/// <summary>
/// A folder a step acts on, or the folder a move puts mails into, in one account. <see cref="Folder"/> is null for a
/// folder that CreateFolder makes at the top of the account.
/// </summary>
public sealed record VoiceFolderTarget(MailItemFolder? Folder, MailAccount Account);

/// <summary>
/// A voice plan checked against the local mailbox and calendars: the preview with each step's count and the steps that
/// can't run turned into banners, and what the runnable steps act on, by command index.
/// </summary>
public sealed record VoicePlanResolution(
    VoicePlanPreview Preview,
    IReadOnlyDictionary<int, IReadOnlyList<VoiceMailTarget>> MailTargets,
    IReadOnlyDictionary<int, IReadOnlyList<VoiceEventTarget>> EventTargets)
{
    public IReadOnlyDictionary<int, IReadOnlyList<VoiceTaskTarget>> TaskTargets { get; init; } = new Dictionary<int, IReadOnlyList<VoiceTaskTarget>>();

    public IReadOnlyDictionary<int, IReadOnlyList<VoiceTaskListTarget>> TaskListTargets { get; init; } = new Dictionary<int, IReadOnlyList<VoiceTaskListTarget>>();

    public IReadOnlyDictionary<int, IReadOnlyList<VoiceContactTarget>> ContactTargets { get; init; } = new Dictionary<int, IReadOnlyList<VoiceContactTarget>>();

    /// <summary>The folder of a folder step, or the destination of a move, per account.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<VoiceFolderTarget>> FolderTargets { get; init; } = new Dictionary<int, IReadOnlyList<VoiceFolderTarget>>();

    /// <summary>The category a Categorize or Uncategorize step assigns, per account.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<MailCategory>> CategoryTargets { get; init; } = new Dictionary<int, IReadOnlyList<MailCategory>>();
}
