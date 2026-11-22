#if DEBUG
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Infrastructure.Diagnostics;
using Wino.Mail.ViewModels.Calendar.Messages;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of the event details (n is the index of the visible events ordered by start): <c>details n</c>,
/// <c>details-state</c>, <c>details-showas Free|Busy|Tentative|OutOfOffice|WorkingElsewhere</c>, <c>details-reminder minutes</c>,
/// <c>details-rsvp accept|tentative|decline</c>, <c>details-rsvp-panel</c>, <c>details-edit</c>, <c>details-edit-series</c>, <c>details-delete</c>,
/// <c>details-join</c>, <c>details-attach open|save index</c>, <c>details-popout</c>, <c>details-windows</c>, <c>details-window-showas key value</c>.
/// </summary>
internal sealed partial class CalendarPageView
{
    partial void RegisterDetailsDebugVerbs()
    {
        Bindings.Own(CalendarDebugActions.AddVerb("details", async args =>
        {
            var items = VisibleItems();
            if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 0 || index >= items.Count) return $"usage: details <0..{items.Count - 1}>";
            var item = items[index];
            Messenger.Send(new CalendarItemTappedMessage(item));
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (_details is EventDetailsView view && view.ViewModel.CurrentEvent?.Id == item.Id && view.ViewModel.SelectedShowAsOption is not null) return view.Describe();
                await CalendarPerfProbe.DelayAsync(50);
            }
            return $"timed out; details={_details?.GetType().Name ?? "-"}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-state", _ => _details is EventDetailsView view ? view.Describe() : "no details"));
        Bindings.Own(CalendarDebugActions.AddVerb("details-showas", async args =>
        {
            if (DetailsView(args, 0) is not { } view || args.Length == 0 || !Enum.TryParse<CalendarItemShowAs>(args[^1], true, out var showAs)) return "usage: details-showas <ShowAs>";
            await view.ChooseShowAsAsync(showAs);
            return $"saved={view.ViewModel.LastMutationSucceeded} {view.Describe()}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-reminder", async args =>
        {
            if (DetailsView(args, 0) is not { } view || args.Length == 0 || !int.TryParse(args[^1], out var minutes)) return "usage: details-reminder <minutes>";
            await view.ToggleReminderAsync(minutes);
            return $"saved={view.ViewModel.LastMutationSucceeded} {view.Describe()}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-reminders", _ => _details is EventDetailsView view ? view.DescribeReminders() : "no details"));
        Bindings.Own(CalendarDebugActions.AddVerb("details-customize", _ =>
        {
            if (_details is not EventDetailsView view) return "no details";
            Observe(view.CustomizeRemindersAsync());
            return "editor requested";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-links", args =>
        {
            if (_details is not EventDetailsView view || view.Links is not { } strip) return "no details";
            if (args is ["open", var index] && int.TryParse(index, out var position) && strip.ItemAt(position) is { } item)
            {
                Observe(view.ViewModel.OpenLinkedItemCommand.ExecuteAsync(item));
                return $"opened '{item.Title}'";
            }
            return $"visible={strip.Root.GetVisible()} followUp={view.CanFollowUp}\n{strip.Describe()}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-followup", _ =>
        {
            if (_details is not EventDetailsView view) return "no details";
            if (!view.CanFollowUp || !view.ViewModel.CreateFollowUpTaskCommand.CanExecute(null)) return "refused";
            view.ViewModel.CreateFollowUpTaskCommand.Execute(null);
            return "follow-up requested";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-rsvp", async args =>
        {
            if (_details is not EventDetailsView view || args.Length == 0) return "usage: details-rsvp accept|tentative|decline";
            var status = args[0] switch { "accept" => AttendeeStatus.Accepted, "tentative" => AttendeeStatus.Tentative, _ => AttendeeStatus.Declined };
            if (!view.ViewModel.SendRsvpResponseCommand.CanExecute(status)) return "refused";
            await view.ViewModel.SendRsvpResponseCommand.ExecuteAsync(status);
            return view.Describe();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-rsvp-panel", _ =>
        {
            if (_details is not EventDetailsView view) return "no details";
            view.ViewModel.ToggleRsvpPanelCommand.Execute(null);
            return view.Describe();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-edit", async _ => await RunDetailsCommand(view => view.ViewModel.EditEventCommand)));
        Bindings.Own(CalendarDebugActions.AddVerb("details-edit-series", async _ => await RunDetailsCommand(view => view.ViewModel.EditEventSeriesCommand)));
        Bindings.Own(CalendarDebugActions.AddVerb("details-delete", async _ => await RunDetailsCommand(view => view.ViewModel.DeleteCommand)));
        Bindings.Own(CalendarDebugActions.AddVerb("details-join", async _ => await RunDetailsCommand(view => view.ViewModel.JoinOnlineCommand)));
        Bindings.Own(CalendarDebugActions.AddVerb("details-attach", args =>
        {
            if (_details is not EventDetailsView view || args.Length < 2 || !int.TryParse(args[1], out var index) || index < 0 || index >= view.ViewModel.Attachments.Count) return "usage: details-attach open|save <index>";
            var attachment = view.ViewModel.Attachments[index];
            if (args[0] == "save") view.SaveAttachment(attachment);
            else view.OpenAttachment(attachment);
            return $"{args[0]} {attachment.FileName}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-popout", async _ =>
        {
            if (_details is not EventDetailsView view || view.ViewModel.CurrentEvent?.CalendarItem is null) return "no details";
            var windows = view.Services.GetRequiredService<WindowManager>();
            view.DebugOpenInNewWindow();
            await CalendarPerfProbe.DelayAsync(1500);
            return string.Join("\n", windows.Describe());
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("details-windows", _ => string.Join("\n", EventDetailsWindow.DebugDescribe())));
    }

    /// <summary>The pane's view, or with a first argument <c>event:&lt;id&gt;</c> the view of that window.</summary>
    private EventDetailsView? DetailsView(string[] args, int keyIndex)
    {
        if (args.Length > keyIndex + 1 && args[keyIndex].StartsWith("event:", StringComparison.Ordinal))
            return EventDetailsWindow.DebugFind(args[keyIndex]);
        return _details as EventDetailsView;
    }

    private async Task<string> RunDetailsCommand(Func<EventDetailsView, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand> command)
    {
        if (_details is not EventDetailsView view) return "no details";
        var relay = command(view);
        if (!relay.CanExecute(null)) return "refused";
        await relay.ExecuteAsync(null);
        return $"done saved={view.ViewModel.LastMutationSucceeded} details={(_details is EventDetailsView ? "open" : "closed")}";
    }
}
#endif
