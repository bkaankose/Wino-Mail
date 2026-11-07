#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// What a voice plan needs from the app's own windows: the steps that end in front of the user (a search, a draft in
/// the composer, an event to review) rather than in a queued request. Each platform implements it.
/// </summary>
public interface IVoicePlanHost
{
    /// <summary>Opens the mail list on <paramref name="folders"/> with <paramref name="criteria"/> applied.</summary>
    Task OpenSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria);

    /// <summary>Opens one mail in the reading pane.</summary>
    Task OpenMailAsync(MailCopy mail);

    /// <summary>
    /// Creates a draft through the request delegator and opens it in the composer. Nothing is sent.
    /// <paramref name="referenceMail"/> is the mail a reply answers.
    /// </summary>
    Task OpenDraftAsync(MailAccount account, DraftCreationOptions options, MailCopy? referenceMail);

    /// <summary>Opens the calendar on an existing event.</summary>
    Task OpenEventAsync(CalendarItem calendarItem);

    /// <summary>Opens the event editor with the planned event filled in, for the user to check and save.</summary>
    Task OpenEventEditorAsync(CalendarEventComposeNavigationArgs args);
}
