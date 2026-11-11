#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Services.Assistant;

/// <summary>
/// Turns the items a request was started from into the runtime's <see cref="AgentSelection"/>: ids, the flags of
/// <see cref="AssistantSelectionFlags"/> that hold for at least one item (the only part that reaches the server, with
/// the kind and the count), and the senders or organizers resolved here so "reply to the sender" needs no round trip.
/// Subjects, bodies, titles, names and addresses never leave the device.
/// </summary>
public static class AgentSelectionBuilder
{
    public static async Task<AgentSelection> BuildAsync(
        VoiceLocalSelection selection,
        IAccountService accountService,
        ICalendarService calendarService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        var own = new HashSet<string>(accounts.SelectMany(static account => new[] { account.Address, account.AuthenticationAddress })
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Select(static address => address.Trim()), StringComparer.OrdinalIgnoreCase);

        var flags = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ItemRef>();
        var senders = new List<Recipient>();
        Guid? accountId = null;

        void AddSender(string? address, string? name)
        {
            if (string.IsNullOrWhiteSpace(address) || !address.Contains('@') || own.Contains(address.Trim()))
                return;

            var trimmed = address.Trim();
            if (!senders.Any(s => string.Equals(s.Address, trimmed, StringComparison.OrdinalIgnoreCase)))
                senders.Add(new Recipient(trimmed, string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
        }

        void Account(Guid id) => accountId = accountId is null || accountId == id ? id : Guid.Empty;

        switch (selection.Kind)
        {
            case AssistantSelectionKind.Mail:
                foreach (var mail in selection.Mails)
                {
                    var mailAccountId = mail.AssignedAccount?.Id ?? mail.AssignedFolder?.MailAccountId;
                    if (mailAccountId is { } id) Account(id);
                    items.Add(new ItemRef(ItemKind.Mail, mailAccountId, mail.UniqueId.ToString()));
                    flags.Add(mail.IsRead ? AssistantSelectionFlags.Read : AssistantSelectionFlags.Unread);
                    if (mail.IsFlagged) flags.Add(AssistantSelectionFlags.Flagged);
                    if (mail.HasAttachments) flags.Add(AssistantSelectionFlags.HasAttachments);
                    if (mail.IsDraft) flags.Add(AssistantSelectionFlags.Draft);
                    if (FolderFlag(mail.AssignedFolder?.SpecialFolderType) is { } folderFlag) flags.Add(folderFlag);
                    if (mail.AssignedAccount?.ProviderType == MailProviderType.Outlook) flags.Add(AssistantSelectionFlags.Outlook);
                    AddSender(mail.FromAddress, mail.FromName);
                }

                break;

            case AssistantSelectionKind.Event:
                foreach (var item in selection.Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var account = accounts.FirstOrDefault(account => account.Id == item.AssignedCalendar?.AccountId);
                    if (account is not null) Account(account.Id);
                    items.Add(new ItemRef(ItemKind.Event, account?.Id, item.Id.ToString()));
                    var isOrganizer = string.IsNullOrWhiteSpace(item.OrganizerEmail) || own.Contains(item.OrganizerEmail.Trim());
                    var attendees = await GetAttendeesAsync(calendarService, item).ConfigureAwait(false);
                    var others = attendees.Where(attendee => !string.IsNullOrWhiteSpace(attendee.Email) && !own.Contains(attendee.Email.Trim())).ToList();

                    flags.Add(isOrganizer ? AssistantSelectionFlags.Organizer : AssistantSelectionFlags.Invitation);
                    if (others.Count > 0) flags.Add(AssistantSelectionFlags.HasAttendees);
                    if (item.IsRecurringEvent) flags.Add(AssistantSelectionFlags.Recurring);
                    if (item.IsAllDayEvent) flags.Add(AssistantSelectionFlags.AllDay);
                    if (CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item, out _)) flags.Add(AssistantSelectionFlags.OnlineMeeting);
                    if (account?.ProviderType == MailProviderType.Outlook) flags.Add(AssistantSelectionFlags.Outlook);

                    if (!isOrganizer) AddSender(item.OrganizerEmail, item.OrganizerDisplayName);
                }

                break;

            case AssistantSelectionKind.Task:
                foreach (var task in selection.Tasks)
                {
                    Account(task.MailAccountId);
                    items.Add(new ItemRef(ItemKind.Task, task.MailAccountId, task.Id.ToString()));
                    if (task.IsCompleted) flags.Add(AssistantSelectionFlags.Completed);
                    else if (task.DueDate is { } due && due.Date < DateTime.Today) flags.Add(AssistantSelectionFlags.Overdue);
                    if (task.IsImportant) flags.Add(AssistantSelectionFlags.Important);
                    if (task.SourceKind == TaskSourceKind.Outlook) flags.Add(AssistantSelectionFlags.Outlook);
                }

                break;

            case AssistantSelectionKind.Contact:
                foreach (var contact in selection.Contacts)
                {
                    Account(contact.MailAccountId);
                    var address = contact.PrimaryEmailAddress;
                    items.Add(string.IsNullOrWhiteSpace(address) ? new ItemRef(ItemKind.Contact, contact.MailAccountId, contact.Id.ToString()) : ItemRef.Contact(address));
                    if (contact.IsFavorite) flags.Add(AssistantSelectionFlags.Favorite);
                }

                break;

            case AssistantSelectionKind.Folder when selection.Folder is { } folder:
                Account(folder.MailAccountId);
                items.Add(ItemRef.Folder(folder.MailAccountId, folder.Id));
                if (FolderFlag(folder.SpecialFolderType) is { } flag) flags.Add(flag);
                if (accounts.FirstOrDefault(account => account.Id == folder.MailAccountId)?.ProviderType == MailProviderType.Outlook)
                    flags.Add(AssistantSelectionFlags.Outlook);
                break;
        }

        // Flags go out in the contract's order, so the same selection always reads the same.
        var ordered = AssistantSelectionFlags.All.Where(flags.Contains).ToList();
        return new AgentSelection(selection.Kind, items, ordered, senders, accountId == Guid.Empty ? null : accountId);
    }

    private static string? FolderFlag(SpecialFolderType? type) => type switch
    {
        SpecialFolderType.Inbox => AssistantSelectionFlags.InInbox,
        SpecialFolderType.Junk => AssistantSelectionFlags.InJunk,
        SpecialFolderType.Archive => AssistantSelectionFlags.InArchive,
        SpecialFolderType.Deleted => AssistantSelectionFlags.InDeleted,
        SpecialFolderType.Sent => AssistantSelectionFlags.InSent,
        _ => null
    };

    private static async Task<List<CalendarEventAttendee>> GetAttendeesAsync(ICalendarService calendarService, CalendarItem item)
    {
        try
        {
            return await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the attendees of a selected event for the assistant.");
            return [];
        }
    }
}
