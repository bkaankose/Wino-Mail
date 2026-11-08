#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Services.Voice;

namespace Wino.Core.Services.Voice;

/// <summary>
/// Context-aware steps: a command with <see cref="VoiceCommand.UsesContext"/> carries no selection of its own and acts
/// on the items the request was started from. They become the same targets a described selection resolves to, so
/// every runner works unchanged. <c>@context</c> tokens name the people of those items.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    /// <summary>The kind of selection an action works on, or null for an action that creates something new.</summary>
    private static VoiceContextKind? ActsOnSelection(VoiceAction action)
        => VoiceCommand.ActsOnMails(action) ? VoiceContextKind.Mail
            : VoiceCommand.ActsOnEvents(action) ? VoiceContextKind.Event
            : VoiceCommand.ActsOnTasks(action) ? VoiceContextKind.Task
            : VoiceCommand.ActsOnContacts(action) ? VoiceContextKind.Contact
            : VoiceCommand.ActsOnFolder(action) ? VoiceContextKind.Folder
            : null;

    private async Task<(VoicePlanPreviewStep Step, StepOutcome Outcome)> ResolveContextStepAsync(
        VoicePlanPreviewStep step,
        VoiceContextKind kind,
        ResolveState state)
    {
        var selection = state.Selection;
        if (selection is null || selection.Kind != kind || selection.Count == 0)
            return (step, StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSelection, PlainText(step))));

        var (text, detail) = VoicePlanPreviewBuilder.DescribeCommand(step.Command, selection.Count);
        step = step with { MarkedText = text, Detail = detail };

        var command = step.Command;
        var index = step.CommandIndex;
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync().ConfigureAwait(false) ?? [];
        MailAccount? AccountOf(Guid? id) => id is Guid accountId ? accounts.FirstOrDefault(account => account.Id == accountId) : null;

        switch (kind)
        {
            case VoiceContextKind.Mail:
            {
                var targets = new List<VoiceMailTarget>();
                foreach (var accountMails in selection.Mails.Where(static mail => mail.AssignedAccount is not null).GroupBy(static mail => mail.AssignedAccount.Id))
                {
                    if (AccountOf(accountMails.Key) is not { } account)
                        continue;

                    var mails = accountMails.DistinctBy(static mail => mail.UniqueId).ToList();
                    var folders = mails.Select(static mail => mail.AssignedFolder).OfType<MailItemFolder>().DistinctBy(static folder => folder.Id).Cast<IMailItemFolder>().ToList();
                    var query = new MailListInitializationOptions(folders, FilterOptionType.All, SortingOptionType.ReceiveDate, CreateThreads: false, IsFocusedOnly: null, SearchQuery: string.Empty, DeduplicateByServerId: true);
                    targets.Add(new VoiceMailTarget(account, query, 0, OldestFirst: false, WithoutAttachments: false, NotFlagged: false, mails.Count, Items: mails));
                }

                if (targets.Count == 0)
                    return (step, StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSelection, PlainText(step))));

                state.MailTargets[index] = targets;
                state.Counts[index] = targets.Sum(static target => target.Count);
                return (step, StepOutcome.Keep(step.Detail));
            }

            case VoiceContextKind.Event:
            {
                var attendeesById = new Dictionary<Guid, List<CalendarEventAttendee>>();
                var targets = new List<VoiceEventTarget>();
                var items = command.Change?.Scope == EventScope.Series
                    ? selection.Events.DistinctBy(static item => item.RecurringCalendarItemId ?? item.Id)
                    : selection.Events.DistinctBy(static item => item.Id);
                foreach (var selected in items)
                {
                    var item = selected.AssignedCalendar is null
                        ? await calendarService.GetCalendarItemAsync(selected.Id).ConfigureAwait(false) ?? selected
                        : selected;
                    if (AccountOf(item.AssignedCalendar?.AccountId) is not { } account)
                        continue;

                    var attendees = await GetAttendeesAsync(item, attendeesById).ConfigureAwait(false);
                    var isOrganizer = string.IsNullOrWhiteSpace(item.OrganizerEmail) || SameAddress(item.OrganizerEmail, account.Address);
                    targets.Add(new VoiceEventTarget(item, account, isOrganizer, attendees.Count(attendee => !SameAddress(attendee.Email, account.Address))));
                }

                string? issue = null;
                var runnable = targets.Count == 0 ? targets : FilterForAction(command, targets, out issue);
                if (runnable.Count == 0)
                    return (step, StepOutcome.Drop(targets.Count == 0 || issue is null ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSelection, PlainText(step)) : issue));

                state.EventTargets[index] = runnable;
                return (step, StepOutcome.Keep(runnable.Count == 1 ? DescribeTargets(runnable, step.Detail) : step.Detail));
            }

            case VoiceContextKind.Task:
            {
                var lists = await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
                var targets = new List<VoiceTaskTarget>();
                foreach (var task in selection.Tasks.DistinctBy(static task => task.Id))
                {
                    if (AccountOf(task.MailAccountId) is { } account)
                        targets.Add(new VoiceTaskTarget(task, lists.FirstOrDefault(list => list.Id == task.TaskListId), account));
                }

                string? issue = null;
                var runnable = targets.Count == 0 ? targets : FilterTasksForAction(command, targets, out issue);
                if (runnable.Count == 0)
                    return (step, StepOutcome.Drop(targets.Count == 0 || issue is null ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSelection, PlainText(step)) : issue));

                state.TaskTargets[index] = runnable;
                return (step, StepOutcome.Keep(runnable.Count == 1 ? DescribeTaskTargets(runnable, step.Detail) : step.Detail));
            }

            case VoiceContextKind.Contact:
            {
                var targets = selection.Contacts
                    .DistinctBy(static contact => contact.Id)
                    .Select(contact => new VoiceContactTarget(contact, AccountOf(contact.MailAccountId)))
                    .ToList();
                state.ContactTargets[index] = targets;
                return (step, StepOutcome.Keep(targets.Count == 1 ? DescribeContactTargets(targets, step.Detail) : step.Detail));
            }

            default:
            {
                if (selection.Folder is not { } folder || AccountOf(folder.MailAccountId) is not { } account)
                    return (step, StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSelection, PlainText(step))));
                if (command.Action is VoiceAction.CreateFolder or VoiceAction.RenameFolder && string.IsNullOrWhiteSpace(command.Folder?.NewName))
                    return (step, StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_FolderName)));
                if (command.Action is VoiceAction.RenameFolder or VoiceAction.DeleteFolder && (folder.SpecialFolderType != SpecialFolderType.Other || folder.IsSystemFolder))
                    return (step, StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_SystemFolder, folder.ResolvedDisplayName)));

                state.FolderTargets[index] = new List<VoiceFolderTarget> { new(folder, account) };
                return (step, StepOutcome.Keep($"{folder.ResolvedDisplayName} · {AccountTitle(account)}"));
            }
        }
    }

    /// <summary>
    /// The addresses of the selection's people that a <c>@context</c> token names: senders, recipients or everyone on
    /// the mails, the attendees or organizer of the events, the contacts. Never the user's own addresses; recipients
    /// come from the stored MIME and are skipped for mails that aren't downloaded.
    /// </summary>
    private async Task<List<string>> PeopleOfSelectionAsync(string part, ResolveState state, CancellationToken cancellationToken)
    {
        if (state.Selection is not { } selection)
            return [];

        if (state.OwnAddresses is null)
        {
            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync().ConfigureAwait(false) ?? [];
            state.OwnAddresses = new HashSet<string>(
                accounts.SelectMany(static account => new[] { account.Address, account.AuthenticationAddress })
                    .Where(static address => !string.IsNullOrWhiteSpace(address))
                    .Select(static address => address.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        var addresses = new List<string>();
        switch (selection.Kind)
        {
            case VoiceContextKind.Mail:
                if (part is "sender" or "senders" or "replyall")
                    addresses.AddRange(selection.Mails.Select(static mail => mail.FromAddress));
                if (part is "recipients" or "replyall")
                {
                    foreach (var mail in selection.Mails.Take(MaxRecipientMails))
                    {
                        if (await ReadMimeAsync(mail, cancellationToken).ConfigureAwait(false) is { } message)
                            addresses.AddRange(message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(static mailbox => mailbox.Address));
                    }
                }

                break;

            case VoiceContextKind.Event:
            {
                var attendeesById = new Dictionary<Guid, List<CalendarEventAttendee>>();
                foreach (var item in selection.Events)
                {
                    if (part != "organizer")
                        addresses.AddRange((await GetAttendeesAsync(item, attendeesById).ConfigureAwait(false)).Select(static attendee => attendee.Email));
                    addresses.Add(item.OrganizerEmail);
                }

                break;
            }

            case VoiceContextKind.Contact:
                addresses.AddRange(selection.Contacts.Select(static contact => contact.PrimaryEmailAddress));
                break;
        }

        var own = state.OwnAddresses;
        return addresses
            .Where(address => !string.IsNullOrWhiteSpace(address) && address.Contains('@') && !own.Contains(address.Trim()))
            .Select(static address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The command a step's items come from: its own, or the one it chains to.</summary>
    private static VoiceCommand? SourceCommand(VoicePlanPreviewStep step, VoicePlanResolution resolution, IReadOnlyCollection<VoicePlanPreviewGroup>? groups)
    {
        if (step.Command.UsesResultOf is not int source)
            return step.Command;

        return resolution.Preview.Groups
            .Concat(groups ?? [])
            .SelectMany(static group => group.Steps)
            .FirstOrDefault(candidate => candidate.CommandIndex == source - 1)?.Command;
    }
}
