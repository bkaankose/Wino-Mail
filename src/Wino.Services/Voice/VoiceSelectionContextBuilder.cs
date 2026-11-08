#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Services.Voice;

/// <summary>
/// Describes a local selection to the server without its content: the kind, the count, the flags of
/// <see cref="VoiceContextFlags"/> that hold for at least one item, and the people by role and address hash. Subjects,
/// bodies, titles, names and addresses never leave the device. Whether a mail offers unsubscribing or holds a
/// verification code is only known from its MIME, so those flags are not sent.
/// </summary>
public static class VoiceSelectionContextBuilder
{
    public static async Task<VoiceSelectionContext> BuildAsync(
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
        var people = new List<VoiceContextPerson>();

        void AddPerson(string role, string? address)
        {
            if (string.IsNullOrWhiteSpace(address) || !address.Contains('@') || own.Contains(address.Trim()))
                return;

            var person = new VoiceContextPerson(role, VoiceBrainHash.Address(address));
            if (!people.Contains(person))
                people.Add(person);
        }

        switch (selection.Kind)
        {
            case VoiceContextKind.Mail:
                foreach (var mail in selection.Mails)
                {
                    flags.Add(mail.IsRead ? VoiceContextFlags.Read : VoiceContextFlags.Unread);
                    if (mail.IsFlagged)
                        flags.Add(VoiceContextFlags.Flagged);
                    if (mail.HasAttachments)
                        flags.Add(VoiceContextFlags.HasAttachments);
                    if (mail.IsDraft)
                        flags.Add(VoiceContextFlags.Draft);
                    if (FolderFlag(mail.AssignedFolder?.SpecialFolderType) is { } folderFlag)
                        flags.Add(folderFlag);
                    if (mail.AssignedAccount?.ProviderType == MailProviderType.Outlook)
                        flags.Add(VoiceContextFlags.Outlook);
                    AddPerson(VoiceContextPerson.Sender, mail.FromAddress);
                }

                break;

            case VoiceContextKind.Event:
                foreach (var item in selection.Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var account = accounts.FirstOrDefault(account => account.Id == item.AssignedCalendar?.AccountId);
                    var isOrganizer = string.IsNullOrWhiteSpace(item.OrganizerEmail) || own.Contains(item.OrganizerEmail.Trim());
                    var attendees = await GetAttendeesAsync(calendarService, item).ConfigureAwait(false);
                    var others = attendees.Where(attendee => !string.IsNullOrWhiteSpace(attendee.Email) && !own.Contains(attendee.Email.Trim())).ToList();

                    flags.Add(isOrganizer ? VoiceContextFlags.Organizer : VoiceContextFlags.Invitation);
                    if (others.Count > 0)
                        flags.Add(VoiceContextFlags.HasAttendees);
                    if (item.IsRecurringEvent)
                        flags.Add(VoiceContextFlags.Recurring);
                    if (item.IsAllDayEvent)
                        flags.Add(VoiceContextFlags.AllDay);
                    if (CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item, out _))
                        flags.Add(VoiceContextFlags.OnlineMeeting);
                    if (account?.ProviderType == MailProviderType.Outlook)
                        flags.Add(VoiceContextFlags.Outlook);

                    if (!isOrganizer)
                        AddPerson(VoiceContextPerson.Organizer, item.OrganizerEmail);
                    foreach (var attendee in others)
                        AddPerson(VoiceContextPerson.Attendee, attendee.Email);
                }

                break;

            case VoiceContextKind.Task:
                foreach (var task in selection.Tasks)
                {
                    if (task.IsCompleted)
                        flags.Add(VoiceContextFlags.Completed);
                    else if (task.DueDate is { } due && due.Date < DateTime.Today)
                        flags.Add(VoiceContextFlags.Overdue);
                    if (task.IsImportant)
                        flags.Add(VoiceContextFlags.Important);
                    if (task.SourceKind == TaskSourceKind.Outlook)
                        flags.Add(VoiceContextFlags.Outlook);
                }

                break;

            case VoiceContextKind.Contact:
                foreach (var contact in selection.Contacts)
                {
                    if (contact.IsFavorite)
                        flags.Add(VoiceContextFlags.Favorite);
                    AddPerson(VoiceContextPerson.Contact, contact.PrimaryEmailAddress);
                }

                break;

            case VoiceContextKind.Folder when selection.Folder is { } folder:
                if (FolderFlag(folder.SpecialFolderType) is { } flag)
                    flags.Add(flag);
                if (accounts.FirstOrDefault(account => account.Id == folder.MailAccountId)?.ProviderType == MailProviderType.Outlook)
                    flags.Add(VoiceContextFlags.Outlook);
                break;
        }

        // Flags go out in the contract's order, so the same selection always reads the same.
        var ordered = VoiceContextFlags.All.Where(flags.Contains).ToList();
        return new VoiceSelectionContext(
            selection.Kind,
            Math.Min(selection.Count, VoiceSelectionContext.MaxCount),
            ordered,
            people.Count == 0 ? null : people.Take(VoiceSelectionContext.MaxPeople).ToList());
    }

    private static string? FolderFlag(SpecialFolderType? type) => type switch
    {
        SpecialFolderType.Inbox => VoiceContextFlags.InInbox,
        SpecialFolderType.Junk => VoiceContextFlags.InJunk,
        SpecialFolderType.Archive => VoiceContextFlags.InArchive,
        SpecialFolderType.Deleted => VoiceContextFlags.InDeleted,
        SpecialFolderType.Sent => VoiceContextFlags.InSent,
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
