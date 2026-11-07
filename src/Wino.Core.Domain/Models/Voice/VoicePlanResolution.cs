#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// The mails one step acts on in one account: the mail list query that finds them and how many it found when the
/// plan was shown. The mails themselves are read again when the step runs, so the result reflects any sync since.
/// </summary>
/// <param name="Limit">At most this many mails, or 0 for every match.</param>
/// <param name="OldestFirst">Take the oldest matches instead of the newest.</param>
/// <param name="WithoutAttachments">Keep only mails without attachments; the query can't express it.</param>
/// <param name="NotFlagged">Keep only mails that aren't flagged; the query can't express it.</param>
public sealed record VoiceMailTarget(
    MailAccount Account,
    MailListInitializationOptions Query,
    int Limit,
    bool OldestFirst,
    bool WithoutAttachments,
    bool NotFlagged,
    int Count);

/// <summary>
/// One calendar event a step acts on. <see cref="IsOrganizer"/>: the account organizes it, so deleting it cancels it
/// for <see cref="AttendeeCount"/> other people and there is no invitation to answer.
/// </summary>
public sealed record VoiceEventTarget(CalendarItem Event, MailAccount Account, bool IsOrganizer, int AttendeeCount);

/// <summary>
/// A voice plan checked against the local mailbox and calendars: the preview with each step's count and the steps that
/// can't run turned into banners, and what the runnable steps act on, by command index.
/// </summary>
public sealed record VoicePlanResolution(
    VoicePlanPreview Preview,
    IReadOnlyDictionary<int, IReadOnlyList<VoiceMailTarget>> MailTargets,
    IReadOnlyDictionary<int, IReadOnlyList<VoiceEventTarget>> EventTargets);
