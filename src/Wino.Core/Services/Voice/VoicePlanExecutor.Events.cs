#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Services.Voice;

/// <summary>
/// Event steps: the device finds the events a plan describes (calendar or account name, title words, a person, a
/// moment or a window) in its own calendars, and changes them through the request delegator like the calendar page
/// does. An action meant for one event that matches several changes nothing: the bubble says which ones matched.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    private const int UpcomingDays = 365;

    private async Task<StepOutcome> ResolveEventStepAsync(
        VoicePlanPreviewGroup group,
        VoicePlanPreviewStep step,
        Dictionary<int, IReadOnlyList<VoiceEventTarget>> eventTargets,
        CancellationToken cancellationToken)
    {
        var command = step.Command;
        if (command.UsesResultOf is int source)
        {
            if (!eventTargets.TryGetValue(source - 1, out var found) || found.Count == 0)
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ChainedSkipped, PlainText(step)));

            var chained = FilterForAction(command, found, out var chainedIssue);
            if (chained.Count == 0)
                return StepOutcome.Drop(chainedIssue ?? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoEvents, PlainText(step)));

            eventTargets[step.CommandIndex] = chained;
            return StepOutcome.Keep(DescribeTargets(chained, step.Detail));
        }

        var accounts = group.Kind switch
        {
            VoicePlanAccountKind.Account when group.Account is { } account => new List<MailAccount> { account },
            VoicePlanAccountKind.AllAccounts => group.Candidates.ToList(),
            _ => new List<MailAccount>()
        };
        if (accounts.Count == 0)
            return StepOutcome.Drop(Translator.VoiceIssue_AccountUnnamed);

        var selection = command.Events ?? new EventSelection(null, null, null, null, 0, EventSortOrder.Earliest, false);
        if (await ResolvePersonAsync(selection.Attendee, command.People).ConfigureAwait(false) is { } attendeeAddress)
            selection = selection with { Attendee = attendeeAddress };

        var (calendars, problem) = await ResolveCalendarsAsync(group, accounts, selection.CalendarName, selection.CalendarId).ConfigureAwait(false);
        if (calendars is null)
            return StepOutcome.Drop(problem ?? Translator.VoiceIssue_AccountUnnamed);

        var now = DateTime.Now;
        var range = selection.TimeRange;
        var isMoment = range is not null && range.StartLocal == range.EndLocal;
        var from = range is null ? now.AddDays(-1) : isMoment ? range.StartLocal.AddDays(-1) : range.StartLocal;
        var to = range is null ? now.AddDays(UpcomingDays) : isMoment ? range.StartLocal.AddDays(1) : range.EndLocal;

        var rows = new List<(CalendarItem Item, MailAccount Account)>();
        foreach (var (calendar, account) in calendars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var items = await calendarService.GetCalendarEventsAsync(calendar, new TimeRange(from, to)).ConfigureAwait(false) ?? [];
                rows.AddRange(items.Select(item => (item, account)));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read the events of a calendar for a voice step.");
            }
        }

        rows = rows.DistinctBy(static row => row.Item.Id).ToList();
        if (range is null)
            rows = rows.Where(row => row.Item.LocalEndDate > now).ToList();
        else if (isMoment)
            rows = rows.Where(row => row.Item.LocalStartDate == range.StartLocal || (row.Item.LocalStartDate <= range.StartLocal && range.StartLocal < row.Item.LocalEndDate)).ToList();
        else
            rows = rows.Where(row => row.Item.LocalStartDate < range.EndLocal && row.Item.LocalEndDate > range.StartLocal).ToList();

        if (!string.IsNullOrWhiteSpace(selection.Title))
        {
            // Either may hold the other: "dentist" for "Dentist appointment", "dentist appointment" for "Dentist".
            var title = selection.Title.Trim();
            rows = rows.Where(row => Fits(row.Item.Title, title) || Fits(title, row.Item.Title)).ToList();
        }

        var attendeesById = new Dictionary<Guid, List<CalendarEventAttendee>>();
        if (!string.IsNullOrWhiteSpace(selection.Attendee))
        {
            var person = selection.Attendee.Trim();
            var matching = new List<(CalendarItem Item, MailAccount Account)>();
            foreach (var row in rows)
            {
                var attendees = await GetAttendeesAsync(row.Item, attendeesById).ConfigureAwait(false);
                var people = attendees.SelectMany(static attendee => new[] { attendee.Name, attendee.Email })
                    .Append(row.Item.OrganizerDisplayName)
                    .Append(row.Item.OrganizerEmail);
                if (people.Any(value => Fits(value, person)))
                    matching.Add(row);
            }

            rows = matching;
        }

        rows = selection.Order == EventSortOrder.Latest
            ? rows.OrderByDescending(static row => row.Item.LocalStartDate).ToList()
            : rows.OrderBy(static row => row.Item.LocalStartDate).ToList();
        // Every occurrence of a series is the same event when the whole series is meant.
        if (command.Change?.Scope == EventScope.Series)
            rows = rows.DistinctBy(static row => row.Item.RecurringCalendarItemId ?? row.Item.Id).ToList();

        // "Join my next meeting" means the next one there is a link to join.
        if (command.Action == VoiceAction.JoinMeeting && rows.Exists(static row => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(row.Item, out _)))
            rows = rows.Where(static row => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(row.Item, out _)).ToList();

        if (selection.Limit > 0)
            rows = rows.Take(selection.Limit).ToList();

        if (rows.Count == 0)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoEvents, PlainText(step)));

        var isMany = selection.All || selection.Limit > 1 || command.Action == VoiceAction.FindEvents;
        if (!isMany && rows.Count > 1)
        {
            // One event was meant and several match: nothing changes until the user says which.
            var names = rows.Take(3).Select(static row => $"{EventTitle(row.Item)} ({When(row.Item)})");
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_WhichEvent, rows.Count, string.Join(", ", names)));
        }

        var targets = new List<VoiceEventTarget>();
        foreach (var (item, account) in rows)
        {
            var attendees = await GetAttendeesAsync(item, attendeesById).ConfigureAwait(false);
            var isOrganizer = string.IsNullOrWhiteSpace(item.OrganizerEmail) || SameAddress(item.OrganizerEmail, account.Address);
            var others = attendees.Count(attendee => !SameAddress(attendee.Email, account.Address));
            targets.Add(new VoiceEventTarget(item, account, isOrganizer, others));
        }

        var runnable = FilterForAction(command, targets, out var issue);
        if (runnable.Count == 0)
            return StepOutcome.Drop(issue ?? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoEvents, PlainText(step)));

        eventTargets[step.CommandIndex] = runnable;
        return StepOutcome.Keep(DescribeTargets(runnable, step.Detail));
    }

    /// <summary>
    /// Drops what an action can't touch: events in read-only calendars for a change, and events the user organizes for an
    /// answer, which has no invitation to answer.
    /// </summary>
    private static List<VoiceEventTarget> FilterForAction(VoiceCommand command, IReadOnlyList<VoiceEventTarget> targets, out string? issue)
    {
        issue = null;
        var result = targets.ToList();

        if (command.Action is VoiceAction.DeleteEvent or VoiceAction.UpdateEvent or VoiceAction.SetEventShowAs or VoiceAction.RespondToEvent or VoiceAction.SetEventReminder)
        {
            var writable = result.Where(static target => target.Event.AssignedCalendar?.IsReadOnly != true).ToList();
            if (writable.Count == 0)
                issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ReadOnlyCalendar, EventTitle(result[0].Event));
            result = writable;
        }

        if (command.Action == VoiceAction.RespondToEvent && result.Count > 0)
        {
            var invited = result.Where(static target => !target.IsOrganizer).ToList();
            if (invited.Count == 0)
                issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_OwnEvent, EventTitle(result[0].Event));
            result = invited;
        }

        if (command.Action == VoiceAction.JoinMeeting && result.Count > 0)
        {
            var online = result.Where(static target => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(target.Event, out _)).ToList();
            if (online.Count == 0)
                issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoJoinLink, EventTitle(result[0].Event));
            result = online;
        }

        return result;
    }

    /// <summary>
    /// The calendars to search. A named calendar matches calendar names first, then account names; when the account is
    /// already named and the words match nothing, they only repeat it ("my Gmail calendar"), so every calendar of that
    /// account is searched.
    /// </summary>
    private async Task<(List<(AccountCalendar Calendar, MailAccount Account)>? Calendars, string? Problem)> ResolveCalendarsAsync(
        VoicePlanPreviewGroup group,
        List<MailAccount> accounts,
        string? calendarName,
        string? calendarId)
    {
        var calendars = new List<(AccountCalendar Calendar, MailAccount Account)>();
        foreach (var account in accounts)
        {
            var accountCalendars = await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
            calendars.AddRange(accountCalendars.Where(static calendar => calendar.IsExtended).Select(calendar => (calendar, account)));
        }

        if (calendars.Count == 0)
            return (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoCalendars, accounts.Count == 1 ? AccountTitle(accounts[0]) : Translator.VoiceBubble_AllAccounts));

        if (!string.IsNullOrWhiteSpace(calendarId))
        {
            var byId = calendars.Where(entry => string.Equals(entry.Calendar.RemoteCalendarId, calendarId, StringComparison.Ordinal)).ToList();
            if (byId.Count > 0)
                return (byId, null);
        }

        if (string.IsNullOrWhiteSpace(calendarName))
            return (calendars, null);

        var name = calendarName.Trim();
        var byName = calendars.Where(entry => Fits(entry.Calendar.Name, name) || Fits(name, entry.Calendar.Name)).ToList();
        if (byName.Count == 0)
            byName = calendars.Where(entry => Fits(entry.Account.Name, name) || Fits(entry.Account.SenderName, name)).ToList();
        if (byName.Count > 0)
            return (byName, null);

        return group.Kind == VoicePlanAccountKind.Account
            ? (calendars, null)
            : (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoCalendarNamed, name));
    }

    private async Task<List<CalendarEventAttendee>> GetAttendeesAsync(CalendarItem item, Dictionary<Guid, List<CalendarEventAttendee>> cache)
    {
        if (cache.TryGetValue(item.Id, out var cached))
            return cached;

        List<CalendarEventAttendee> attendees;
        try
        {
            attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the attendees of an event for a voice step.");
            attendees = [];
        }

        cache[item.Id] = attendees;
        return attendees;
    }

    private static string DescribeTargets(IReadOnlyList<VoiceEventTarget> targets, string? detail)
    {
        if (targets.Count == 1)
        {
            var only = targets[0].Event;
            return $"{EventTitle(only)} · {When(only)}";
        }

        var found = string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_EventsFound, targets.Count);
        return string.IsNullOrWhiteSpace(detail) ? found : $"{found} · {detail}";
    }

    private static string EventTitle(CalendarItem item)
        => string.IsNullOrWhiteSpace(item.Title) ? Translator.VoiceResult_NoSubject : item.Title.Replace("**", string.Empty).Trim();

    private static string When(CalendarItem item)
        => item.IsAllDayEvent
            ? item.LocalStartDate.ToString("D", CultureInfo.CurrentCulture)
            : item.LocalStartDate.ToString("f", CultureInfo.CurrentCulture);

    private static string EventsText(int count)
        => count == 1 ? Translator.VoiceCount_Event : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Events, count);

    private static bool SameAddress(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> contains <paramref name="part"/>, ignoring case and accents ("Ayşe" and "ayse").</summary>
    private static bool Fits(string? text, string? part)
        => !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(part)
           && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, part.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private void AddEventRisks(VoicePlanResolution resolution, VoicePlanPreviewGroup group, VoicePlanPreviewStep step, List<VoicePlanRisk> risks)
    {
        if (!resolution.EventTargets.TryGetValue(step.CommandIndex, out var targets) || targets.Count == 0)
            return;

        var command = step.Command;
        var events = EventsText(targets.Count);
        var meeting = targets.FirstOrDefault(static target => target.IsOrganizer && target.AttendeeCount > 0);

        switch (command.Action)
        {
            case VoiceAction.DeleteEvent when meeting is not null:
                risks.Add(new VoicePlanRisk(step.CommandIndex,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_CancelMeeting, EventTitle(meeting.Event), meeting.AttendeeCount),
                    false,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteButton, events)));
                break;
            case VoiceAction.DeleteEvent when command.Change?.Scope == EventScope.Series:
                risks.Add(new VoicePlanRisk(step.CommandIndex,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteSeries, targets.Count == 1 ? EventTitle(targets[0].Event) : events),
                    false,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteButton, events)));
                break;
            case VoiceAction.DeleteEvent when targets.Count > 1:
                risks.Add(new VoicePlanRisk(step.CommandIndex,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteEvents, events),
                    false,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteButton, events)));
                break;
            case VoiceAction.UpdateEvent when targets.Count > 1 || meeting is not null:
                risks.Add(new VoicePlanRisk(step.CommandIndex,
                    meeting is not null
                        ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_UpdateMeeting, EventTitle(meeting.Event), meeting.AttendeeCount)
                        : string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_UpdateEvents, events),
                    false,
                    Translator.VoiceGuard_Continue));
                break;
            case VoiceAction.RespondToEvent when command.Change?.Response == EventResponse.Decline && targets.Count > 1:
                risks.Add(new VoicePlanRisk(step.CommandIndex,
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeclineEvents, events),
                    false,
                    Translator.VoiceGuard_Continue));
                break;
        }
    }

    private async Task<StepResult> RunEventStepAsync(StepContext context)
    {
        var command = context.Step.Command;
        var index = context.Step.CommandIndex;
        if (!context.Resolution.EventTargets.TryGetValue(index, out var found) || found.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoEventsLeft);

        var targets = command.Change?.Scope == EventScope.Series && command.Action != VoiceAction.FindEvents
            ? await ToSeriesAsync(found).ConfigureAwait(false)
            : found;

        switch (command.Action)
        {
            case VoiceAction.FindEvents:
            {
                // Opening an event shows its day; a timed event is the one the user is after, not a holiday that spans it.
                var shown = targets.OrderBy(static target => target.Event.IsAllDayEvent).ThenBy(static target => target.Event.StartDate).First().Event;
                await context.Host.OpenEventAsync(shown).ConfigureAwait(false);
                return StepResult.Done(targets.Count == 1
                    ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventOpened, EventTitle(shown), When(shown))
                    : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventsFound, EventsText(targets.Count), EventTitle(shown)));
            }

            case VoiceAction.DeleteEvent:
                foreach (var target in targets)
                    await requestDelegator.ExecuteAsync(new CalendarOperationPreparationRequest(CalendarSynchronizerOperation.DeleteEvent, target.Event)).ConfigureAwait(false);

                return StepResult.Done(targets.Count == 1
                    ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventDeleted, EventTitle(targets[0].Event))
                    : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, EventsText(targets.Count)));

            case VoiceAction.UpdateEvent:
                return await UpdateEventsAsync(command, targets).ConfigureAwait(false);

            case VoiceAction.SetEventShowAs:
                {
                    if (command.Change?.ShowAs is not { } showAs)
                        return StepResult.Failed(Translator.VoiceIssue_Missing_Change);

                    foreach (var target in targets)
                    {
                        await SaveEventAsync(target, CalendarSynchronizerOperation.UpdateEvent, item => item.ShowAs = (CalendarItemShowAs)(int)showAs).ConfigureAwait(false);
                    }

                    return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventShowAs,
                        targets.Count == 1 ? EventTitle(targets[0].Event) : EventsText(targets.Count),
                        Wino.Services.Voice.VoicePlanPreviewBuilder.ShowAsName(showAs)));
                }

            case VoiceAction.RespondToEvent:
                {
                    var response = command.Change?.Response ?? EventResponse.Accept;
                    var operation = response switch
                    {
                        EventResponse.Accept => CalendarSynchronizerOperation.AcceptEvent,
                        EventResponse.Tentative => CalendarSynchronizerOperation.TentativeEvent,
                        _ => CalendarSynchronizerOperation.DeclineEvent
                    };

                    var message = command.Change?.ResponseMessage?.Trim();
                    foreach (var target in targets)
                    {
                        var request = string.IsNullOrEmpty(message)
                            ? new CalendarOperationPreparationRequest(operation, target.Event)
                            : new CalendarOperationPreparationRequest(operation, target.Event, ResponseMessage: message);
                        await requestDelegator.ExecuteAsync(request).ConfigureAwait(false);
                    }

                    var template = response switch
                    {
                        EventResponse.Accept => Translator.VoiceResult_EventAccepted,
                        EventResponse.Tentative => Translator.VoiceResult_EventTentative,
                        _ => Translator.VoiceResult_EventDeclined
                    };
                    return StepResult.Done(string.Format(CultureInfo.CurrentCulture, template,
                        targets.Count == 1 ? EventTitle(targets[0].Event) : EventsText(targets.Count)));
                }

            case VoiceAction.SetEventReminder:
                {
                    if (command.Change?.ReminderMinutes is not { } minutes || minutes < 0)
                        return StepResult.Failed(Translator.VoiceIssue_Missing_Change);

                    foreach (var target in targets)
                    {
                        var reminders = new List<Reminder>
                        {
                            new() { Id = Guid.NewGuid(), CalendarItemId = target.Event.Id, DurationInSeconds = minutes * 60L, ReminderType = CalendarItemReminderType.Popup }
                        };
                        await SaveEventAsync(target, CalendarSynchronizerOperation.UpdateEvent, static _ => { }, reminders).ConfigureAwait(false);
                    }

                    return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ReminderSet,
                        targets.Count == 1 ? EventTitle(targets[0].Event) : EventsText(targets.Count),
                        Wino.Services.Voice.VoicePlanPreviewBuilder.MinutesText(minutes)));
                }

            case VoiceAction.JoinMeeting:
                {
                    var meeting = targets.FirstOrDefault(static target => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(target.Event, out _));
                    if (meeting is null || !CalendarJoinLinkResolver.TryGetEffectiveJoinUri(meeting.Event, out var uri) || uri is null)
                        return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoJoinLink, EventTitle(targets[0].Event)));
                    if (services.GetService<IExternalLauncher>() is not { } launcher)
                        return StepResult.Failed(Translator.VoiceResult_LaunchUnavailable);

                    var launched = await launcher.LaunchUriAsync(uri).ConfigureAwait(false);
                    return launched.IsSuccess
                        ? StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Joined, EventTitle(meeting.Event)))
                        : StepResult.Failed(launched.ErrorMessage ?? Translator.VoiceResult_LaunchUnavailable);
                }
        }

        return StepResult.None;
    }

    /// <summary>The series events stand for, for a change meant for the whole series; single events stay as they are.</summary>
    private async Task<IReadOnlyList<VoiceEventTarget>> ToSeriesAsync(IReadOnlyList<VoiceEventTarget> targets)
    {
        var result = new List<VoiceEventTarget>();
        foreach (var target in targets)
        {
            if (!target.Event.IsRecurringEvent)
            {
                result.Add(target);
                continue;
            }

            var series = await calendarService.GetCalendarItemTargetAsync(new CalendarItemTarget(target.Event, CalendarEventTargetType.Series)).ConfigureAwait(false);
            result.Add(series is null ? target : target with { Event = series });
        }

        return result.DistinctBy(static target => target.Event.Id).ToList();
    }

    private async Task<StepResult> UpdateEventsAsync(VoiceCommand command, IReadOnlyList<VoiceEventTarget> targets)
    {
        if (command.Change is not { } change)
            return StepResult.Failed(Translator.VoiceIssue_Missing_Change);

        var timeChanged = change.NewDate is not null || change.NewStartTime is not null || change.ShiftMinutes is not null || change.DurationMinutes is not null
            || change.Slot is not null;
        var detailsChanged = !string.IsNullOrWhiteSpace(change.NewTitle) || !string.IsNullOrWhiteSpace(change.NewLocation);
        if (!timeChanged && !detailsChanged)
            return StepResult.Failed(Translator.VoiceIssue_Missing_Change);

        DateTime? lastStart = null;
        foreach (var target in targets)
        {
            var current = target.Event;
            var start = current.LocalStartDate;
            var length = current.LocalEndDate - current.LocalStartDate;
            var isAllDay = current.IsAllDayEvent;

            if (change.NewDate is { } day)
                start = day.ToDateTime(TimeOnly.FromDateTime(start));

            if (change.NewStartTime is { } time)
            {
                start = start.Date + time.ToTimeSpan();
                if (isAllDay)
                {
                    // A time for an all-day event makes it a timed one, an hour long unless the user said otherwise.
                    isAllDay = false;
                    length = TimeSpan.FromHours(1);
                }
            }

            if (change.ShiftMinutes is { } shift)
            {
                start = start.AddMinutes(shift);
                isAllDay = isAllDay && shift % (24 * 60) == 0;
            }

            if (change.DurationMinutes is { } minutes && minutes > 0)
            {
                length = TimeSpan.FromMinutes(minutes);
                isAllDay = false;
            }

            if (change.Slot is { } slot)
            {
                if (slot.DurationMinutes > 0)
                    length = TimeSpan.FromMinutes(slot.DurationMinutes);
                else if (isAllDay)
                    length = TimeSpan.FromHours(1);

                if (await FindFreeSlotAsync(slot, length, current.Id).ConfigureAwait(false) is not { } free)
                    return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoFreeSlot, EventTitle(current)));

                start = free;
                isAllDay = false;
            }

            var newStart = start;
            var newLength = isAllDay ? TimeSpan.FromDays(Math.Max(1, Math.Round(length.TotalDays))) : length;
            await SaveEventAsync(target, timeChanged && !detailsChanged ? CalendarSynchronizerOperation.ChangeStartAndEndDate : CalendarSynchronizerOperation.UpdateEvent, item =>
            {
                item.StartDate = isAllDay ? newStart.Date : newStart.ToTimeZoneFromLocal(item.StartTimeZone);
                item.DurationInSeconds = newLength.TotalSeconds;
                if (!string.IsNullOrWhiteSpace(change.NewTitle))
                    item.Title = change.NewTitle.Trim();
                if (!string.IsNullOrWhiteSpace(change.NewLocation))
                    item.Location = change.NewLocation.Trim();
            }).ConfigureAwait(false);

            lastStart = newStart;
        }

        var name = targets.Count == 1 ? EventTitle(targets[0].Event) : EventsText(targets.Count);
        return StepResult.Done(timeChanged && lastStart is { } moved && targets.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventMoved, name, moved.ToString("f", CultureInfo.CurrentCulture))
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventUpdated, name));
    }

    /// <summary>
    /// The start of a free slot across every calendar on this device. Free events and all-day events that don't show as
    /// out of office leave the time free; <paramref name="excludedEventId"/> is the event being moved.
    /// </summary>
    private async Task<DateTime?> FindFreeSlotAsync(EventSlotRequest slot, TimeSpan length, Guid? excludedEventId)
    {
        var period = new TimeRange(slot.From.ToDateTime(TimeOnly.MinValue), slot.To.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var busy = new List<(DateTime Start, DateTime End)>();
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync().ConfigureAwait(false) ?? [];
        foreach (var account in accounts.Where(static account => account.IsCalendarAccessGranted && account.IsCalendarAccessEnabled))
        {
            foreach (var calendar in await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [])
            {
                var items = await calendarService.GetCalendarEventsAsync(calendar, period).ConfigureAwait(false) ?? [];
                busy.AddRange(items
                    .Where(item => !item.IsHidden && item.Id != excludedEventId && item.ShowAs != CalendarItemShowAs.Free
                        && (!item.IsAllDayEvent || item.ShowAs == CalendarItemShowAs.OutOfOffice))
                    .Select(static item => (item.LocalStartDate, item.LocalEndDate)));
            }
        }

        var workingHours = preferencesService.IsWorkingHoursEnabled && preferencesService.WorkingHourStart >= TimeSpan.Zero
            && preferencesService.WorkingHourEnd > preferencesService.WorkingHourStart && preferencesService.WorkingHourEnd < TimeSpan.FromDays(1);
        return FreeSlotFinder.Find(slot, length, busy, DateTime.Now,
            workingHours ? TimeOnly.FromTimeSpan(preferencesService.WorkingHourStart) : null,
            workingHours ? TimeOnly.FromTimeSpan(preferencesService.WorkingHourEnd) : null);
    }

    /// <summary>
    /// Changes an event the way the calendar page does: the stored copy is updated first, then the request carries the
    /// original so the synchronizer can send only what changed and roll back on failure.
    /// </summary>
    private async Task SaveEventAsync(VoiceEventTarget target, CalendarSynchronizerOperation operation, Action<CalendarItem> change, List<Reminder>? newReminders = null)
    {
        var item = await calendarService.GetCalendarItemAsync(target.Event.Id).ConfigureAwait(false) ?? target.Event;
        var original = await calendarService.GetCalendarItemAsync(target.Event.Id).ConfigureAwait(false) ?? target.Event;
        item.AssignedCalendar ??= target.Event.AssignedCalendar;
        original.AssignedCalendar ??= target.Event.AssignedCalendar;

        var attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        var originalAttendees = await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        var reminders = await calendarService.GetRemindersAsync(item.Id).ConfigureAwait(false) ?? [];

        change(item);
        await calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
        if (newReminders is not null)
            await calendarService.SaveRemindersAsync(item.Id, newReminders).ConfigureAwait(false);

        await requestDelegator.ExecuteAsync(new CalendarOperationPreparationRequest(
            operation,
            item,
            attendees,
            OriginalItem: original,
            OriginalAttendees: originalAttendees,
            Reminders: newReminders ?? reminders,
            OriginalReminders: reminders)).ConfigureAwait(false);
    }
}
