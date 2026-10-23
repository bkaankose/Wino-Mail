#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MailCal.Calendar.ViewModels;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Exceptions;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Mail.ViewModels.Companion;

public sealed class CompanionActionHandler(
    IServiceProvider services,
    CompanionNavigationCallbacks navigation) : ICompanionActionHandler
{
    public Task OpenMailCalAsync(CancellationToken cancellationToken) => navigation.OpenMailCal(cancellationToken);

    public Task OpenCalendarAsync(CancellationToken cancellationToken) => navigation.OpenCalendar(cancellationToken);

    public async Task OpenCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken)
    {
        var stored = await services.GetRequiredService<ICalendarService>()
            .GetCalendarItemAsync(calendarItem.Id)
            .ConfigureAwait(false);
        if (stored?.AssignedCalendar is { } calendar && calendar.AccountId == calendarItem.AssignedCalendar?.AccountId)
            await navigation.OpenCalendarEvent(calendar.AccountId, stored.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task OpenEventEditorAsync(CompanionEventDraft draft, CancellationToken cancellationToken)
        => navigation.NewEvent(draft, cancellationToken);

    public Task OpenSettingsAsync(CancellationToken cancellationToken) => navigation.OpenSettings(cancellationToken);

    public async Task CreateQuickEventAsync(CompanionEventDraft draft, CancellationToken cancellationToken)
    {
        // Re-read the calendar: it may have been removed or turned read-only since the form was filled.
        var calendar = draft.CalendarId is { } calendarId
            ? await services.GetRequiredService<ICalendarService>().GetAccountCalendarAsync(calendarId).ConfigureAwait(false)
            : null;
        if (calendar is null || calendar.IsReadOnly)
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationMissingCalendar);

        cancellationToken.ThrowIfCancellationRequested();
        var composeResult = QuickEventFactory.Create(calendar, draft.Title.Trim(), null, draft.Start, draft.End, draft.IsAllDay);
        await QuickEventFactory.CreateAsync(services.GetRequiredService<IMailCalRequestDelegator>(), composeResult)
            .ConfigureAwait(false);
    }
}
