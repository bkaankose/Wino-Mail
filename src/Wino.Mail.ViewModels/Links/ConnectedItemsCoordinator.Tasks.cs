#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Links;

namespace Wino.Mail.ViewModels.Links;

public sealed partial class ConnectedItemsCoordinator
{
    /// <summary>Adds a follow-up task for an event, due on the event's day, linked to the event.</summary>
    public async Task CreateTaskFromEventAsync(CalendarItem calendarItem)
    {
        ArgumentNullException.ThrowIfNull(calendarItem);

        var accountId = calendarItem.AssignedCalendar?.AccountId
            ?? (await calendarService.GetAccountCalendarAsync(calendarItem.CalendarId).ConfigureAwait(false))?.AccountId
            ?? Guid.Empty;
        var eventTitle = string.IsNullOrWhiteSpace(calendarItem.Title) ? Translator.MailItemNoSubject : calendarItem.Title.Trim();
        var start = calendarItem.LocalStartDate;

        await CreateTaskAsync(
            new TaskSeed(
                string.Format(CultureInfo.CurrentCulture, Translator.ConnectedItems_FollowUpTitleFormat, eventTitle),
                string.Empty,
                start.Date,
                accountId == Guid.Empty ? null : accountId),
            new ItemLink
            {
                SourceKind = ItemLinkKind.Event,
                SourceAccountId = accountId,
                SourceKey = ItemLinkKeys.ForEvent(calendarItem),
                SourceTitle = eventTitle,
                SourceSubtitle = start.ToString("g", CultureInfo.CurrentCulture),
                TargetKind = ItemLinkKind.Task,
                Relation = ItemLinkRelation.FollowUp
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the event composer with a one-hour block for the task: on its due day when that is still ahead,
    /// otherwise at the next full hour. The block is linked to the task once the server confirms it.
    /// </summary>
    public async Task ScheduleTimeForTaskAsync(AccountTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var account = await accountService.GetAccountAsync(task.MailAccountId).ConfigureAwait(false);
        var calendar = await GetDefaultWritableCalendarAsync(task.MailAccountId).ConfigureAwait(false);
        var now = DateTime.Now;
        var start = task.DueDate?.Date is { } due && due > now.Date
            ? due.AddHours(9)
            : NextFullHour(now);
        var title = task.Title?.Trim() ?? string.Empty;

        var args = new CalendarEventComposeNavigationArgs
        {
            SelectedCalendarId = calendar?.Id,
            RequireCalendarPickerWhenUnresolved = calendar is null,
            AccountAddressHints = account?.Address is { Length: > 0 } address ? [address] : [],
            Title = title,
            NotesHtml = ToHtmlParagraph(task.Notes),
            StartDate = start,
            EndDate = start.AddHours(1),
            PendingLinks =
            [
                new ItemLink
                {
                    SourceKind = ItemLinkKind.Task,
                    SourceAccountId = task.MailAccountId,
                    SourceKey = ItemLinkKeys.ForTask(task),
                    SourceTitle = title,
                    TargetKind = ItemLinkKind.Event,
                    TargetAccountId = calendar?.AccountId ?? task.MailAccountId,
                    TargetTitle = title,
                    Relation = ItemLinkRelation.TimeBlock
                }
            ]
        };

        await OpenEventComposerAsync(args).ConfigureAwait(false);
    }
}
