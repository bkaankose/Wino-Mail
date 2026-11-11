#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Services.Assistant;

/// <summary>
/// The read-only facade the Wino Agent Runtime resolves against (plan section 4.3): accounts, folders, calendars,
/// lists, categories and contact lists as entities; contact and sender lookups from the address books and the
/// recipient history; counts and item lookups through the mail, calendar and task services. Nothing here changes
/// data and nothing leaves the device.
/// </summary>
public sealed class AgentDomainQueries(
    IAccountService accountService,
    IFolderService folderService,
    ICalendarService calendarService,
    ITaskQueryService taskQueryService,
    IMailCategoryService categoryService,
    IContactService contactService,
    IContactQueryService contactQueryService,
    IRecipientHistoryService recipientHistory,
    IMailService mailService) : IAgentDomainQueries
{
    private const int TopCorrespondents = 30;

    /// <summary>Folders a broad search leaves out: drafts, deleted and junk mail, and the views that only repeat mails stored elsewhere.</summary>
    public static readonly HashSet<SpecialFolderType> ExcludedFromBroadSearch =
    [
        SpecialFolderType.Draft, SpecialFolderType.Deleted, SpecialFolderType.Junk, SpecialFolderType.Chat, SpecialFolderType.Starred,
        SpecialFolderType.Important, SpecialFolderType.Unread, SpecialFolderType.Category, SpecialFolderType.Forums, SpecialFolderType.Updates,
        SpecialFolderType.Personal, SpecialFolderType.Promotions, SpecialFolderType.Social, SpecialFolderType.More
    ];

    public async Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        return accounts.OrderBy(static a => a.Order).ToList();
    }

    public async Task<IReadOnlyList<MailAccountAlias>> GetAliasesAsync(Guid accountId, CancellationToken ct)
        => await accountService.GetAccountAliasesAsync(accountId).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<MailItemFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct)
    {
        var folders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false) ?? [];
        return folders.Where(static f => !f.IsHidden).ToList();
    }

    public async Task<IReadOnlyList<AccountCalendar>> GetCalendarsAsync(Guid accountId, CancellationToken ct)
        => await calendarService.GetAccountCalendarsAsync(accountId).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<AccountTaskList>> GetTaskListsAsync(Guid accountId, CancellationToken ct)
        => await taskQueryService.GetTaskListsAsync(accountId).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<MailCategory>> GetCategoriesAsync(Guid accountId, CancellationToken ct)
        => await categoryService.GetCategoriesAsync(accountId).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<ContactList>> GetContactListsAsync(CancellationToken ct)
        => await contactQueryService.GetContactListsAsync().ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<AgentContact>> SearchContactsAsync(string query, int limit, CancellationToken ct)
    {
        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        var text = (query ?? string.Empty).Trim();
        var result = new List<AgentContact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The people the user writes to most come first, searched or not.
        var frequent = new List<RecipientHistory>();
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entries = text.Length == 0
                    ? await recipientHistory.GetMostFrequentAsync(account.Id, TopCorrespondents).ConfigureAwait(false)
                    : await recipientHistory.SearchAsync(account.Id, text, limit).ConfigureAwait(false);
                frequent.AddRange(entries ?? []);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read the recipient history for the assistant.");
            }
        }

        foreach (var entry in frequent.OrderByDescending(static e => e.SentCount * 3 + e.ReceivedCount))
        {
            if (string.IsNullOrWhiteSpace(entry.Address) || !seen.Add(entry.Address)) continue;
            if (text.Length > 0 && !StartsWithAnyWord(text, entry.DisplayName, entry.Address)) continue;
            result.Add(new AgentContact(null, entry.Address.Trim(), Blank(entry.DisplayName), true, entry.AccountId));
            if (result.Count >= limit) return result;
        }

        if (text.Length == 0) return result;

        List<AccountContact> contacts;
        try { contacts = await contactService.ResolveRecipientCandidatesAsync(null, text, limit).ConfigureAwait(false) ?? []; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not search the contacts for the assistant.");
            contacts = [];
        }

        foreach (var contact in contacts)
        {
            foreach (var email in contact.EmailAddresses.OrderByDescending(static a => a.IsPrimary).ThenBy(static a => a.Order))
            {
                if (string.IsNullOrWhiteSpace(email.Address) || !seen.Add(email.Address)) continue;
                result.Add(new AgentContact(contact.Id, email.Address.Trim(), Blank(contact.DisplayName), false, contact.MailAccountId));
                if (result.Count >= limit) return result;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<AgentSender>> SearchSendersAsync(string query, int limit, CancellationToken ct)
    {
        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        var text = (query ?? string.Empty).Trim();
        var byAddress = new Dictionary<string, (string? Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            List<RecipientHistory> entries;
            try
            {
                entries = text.Length == 0
                    ? await recipientHistory.GetMostFrequentAsync(account.Id, limit).ConfigureAwait(false) ?? []
                    : await recipientHistory.SearchAsync(account.Id, text, limit).ConfigureAwait(false) ?? [];
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read the sender index for the assistant.");
                continue;
            }

            foreach (var entry in entries.Where(static e => e.ReceivedCount > 0 && !string.IsNullOrWhiteSpace(e.Address)))
            {
                if (text.Length > 0 && !ContainsFolded(text, entry.DisplayName, entry.Address)) continue;
                byAddress[entry.Address] = byAddress.TryGetValue(entry.Address, out var known)
                    ? (known.Name ?? Blank(entry.DisplayName), known.Count + entry.ReceivedCount)
                    : (Blank(entry.DisplayName), entry.ReceivedCount);
            }
        }

        return byAddress
            .OrderByDescending(static pair => pair.Value.Count)
            .Take(limit)
            .Select(static pair => new AgentSender(pair.Key.Trim(), pair.Value.Name, Domain(pair.Key), pair.Value.Count))
            .ToList();
    }

    public async Task<int> CountMailsAsync(Guid? accountId, MailQuery query, CancellationToken ct)
    {
        var accounts = accountId is { } id ? [await accountService.GetAccountAsync(id).ConfigureAwait(false)] : await GetAccountsAsync(ct).ConfigureAwait(false);
        var total = 0;
        foreach (var account in accounts.Where(static a => a is not null))
        {
            ct.ThrowIfCancellationRequested();
            var folders = await SearchFoldersAsync(account.Id, query).ConfigureAwait(false);
            if (folders.Count == 0) continue;
            var options = ToOptions(folders, query);
            total += await mailService.CountMailsAsync(options, ct).ConfigureAwait(false);
        }

        return total;
    }

    public async Task<IReadOnlyList<AgentEventSummary>> FindEventsAsync(Guid? accountId, EventQuery query, int limit, CancellationToken ct)
    {
        var now = DateTime.Now;
        var from = query.From ?? now.Date.AddDays(-7);
        var to = query.To ?? now.Date.AddDays(60);
        if (to <= from) to = from.AddDays(1);
        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        var own = OwnAddresses(accounts);
        var result = new List<AgentEventSummary>();
        var wantedCalendars = query.CalendarIds.Count == 0 ? null : new HashSet<Guid>(query.CalendarIds);
        var needsAttendees = query.AttendeeAddresses.Count > 0 || !string.IsNullOrWhiteSpace(query.AttendeeWords);

        foreach (var account in accounts.Where(a => accountId is null || a.Id == accountId))
        {
            foreach (var calendar in await GetCalendarsAsync(account.Id, ct).ConfigureAwait(false))
            {
                if (wantedCalendars is not null && !wantedCalendars.Contains(calendar.Id)) continue;
                ct.ThrowIfCancellationRequested();
                List<CalendarItem> items;
                try { items = await calendarService.GetCalendarEventsAsync(calendar, new TimeRange(from, to)).ConfigureAwait(false) ?? []; }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not read the events of a calendar for the assistant.");
                    continue;
                }

                foreach (var item in items.Where(static i => !i.IsHidden && i.Status != CalendarItemStatus.Cancelled).OrderBy(static i => i.StartDate))
                {
                    if (!string.IsNullOrWhiteSpace(query.TitleWords) && !ContainsFolded(query.TitleWords, item.Title, item.Location, item.Description)) continue;
                    var isOrganizer = string.IsNullOrWhiteSpace(item.OrganizerEmail) || own.Contains(item.OrganizerEmail.Trim());
                    var attendeeCount = 0;
                    if (needsAttendees)
                    {
                        var attendees = await GetAttendeesAsync(item).ConfigureAwait(false);
                        attendeeCount = attendees.Count(a => !string.IsNullOrWhiteSpace(a.Email) && !own.Contains(a.Email.Trim()));
                        var matches = query.AttendeeAddresses.Any(address => attendees.Any(a => string.Equals(a.Email?.Trim(), address, StringComparison.OrdinalIgnoreCase)))
                            || (!string.IsNullOrWhiteSpace(query.AttendeeWords) && attendees.Any(a => ContainsFolded(query.AttendeeWords, a.Name, a.Email)));
                        if (!matches) continue;
                    }

                    result.Add(new AgentEventSummary(item.Id, account.Id, calendar.Id, item.Title ?? string.Empty, item.StartDate, item.EndDate, item.IsAllDayEvent, isOrganizer, attendeeCount, item.IsRecurringEvent));
                    if (limit > 0 && result.Count >= limit) return result;
                }
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<AgentTaskSummary>> FindTasksAsync(Guid? accountId, TaskQuery query, int limit, CancellationToken ct)
    {
        var view = query.MyDay ? TaskViewKind.MyDay : query.Important ? TaskViewKind.Important : TaskViewKind.All;
        var listId = query.ListIds.Count == 1 ? query.ListIds[0] : (Guid?)null;
        List<AccountTask> tasks;
        try { tasks = await taskQueryService.GetTasksAsync(accountId, listId, view, Blank(query.TitleWords), TaskSortKind.DueDate).ConfigureAwait(false) ?? []; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the tasks for the assistant.");
            return [];
        }

        var wantedLists = query.ListIds.Count > 1 ? new HashSet<Guid>(query.ListIds) : null;
        var today = DateTime.Today;
        var result = new List<AgentTaskSummary>();
        foreach (var task in tasks)
        {
            if (wantedLists is not null && !wantedLists.Contains(task.TaskListId)) continue;
            if (!query.IncludeCompleted && task.IsCompleted) continue;
            if (query.Overdue && (task.IsCompleted || task.DueDate is not { } overdue || overdue.Date >= today)) continue;
            if (query.DueFrom is { } dueFrom && (task.DueDate is not { } d1 || d1 < dueFrom)) continue;
            if (query.DueTo is { } dueTo && (task.DueDate is not { } d2 || d2 > dueTo)) continue;
            if (!string.IsNullOrWhiteSpace(query.TitleWords) && !ContainsFolded(query.TitleWords, task.Title)) continue;
            result.Add(new AgentTaskSummary(task.Id, task.MailAccountId, task.TaskListId, task.Title ?? string.Empty, task.DueDate, task.IsCompleted, task.IsImportant));
            if (limit > 0 && result.Count >= limit) break;
        }

        return result;
    }

    public async Task<IReadOnlyList<AgentBusyInterval>> GetBusyIntervalsAsync(Guid? accountId, DateTime from, DateTime to, CancellationToken ct)
    {
        var result = new List<AgentBusyInterval>();
        if (to <= from) return result;
        foreach (var account in (await GetAccountsAsync(ct).ConfigureAwait(false)).Where(a => accountId is null || a.Id == accountId))
        {
            foreach (var calendar in await GetCalendarsAsync(account.Id, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                List<CalendarItem> items;
                try { items = await calendarService.GetCalendarEventsAsync(calendar, new TimeRange(from, to)).ConfigureAwait(false) ?? []; }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not read the busy intervals of a calendar for the assistant.");
                    continue;
                }

                result.AddRange(items
                    .Where(static i => !i.IsHidden && i.Status != CalendarItemStatus.Cancelled && i.ShowAs != CalendarItemShowAs.Free)
                    .Select(static i => new AgentBusyInterval(i.StartDate, i.EndDate)));
            }
        }

        return result.OrderBy(static b => b.Start).ToList();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<List<IMailItemFolder>> SearchFoldersAsync(Guid accountId, MailQuery query)
    {
        var folders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false) ?? [];
        if (query.FolderIds.Count > 0)
        {
            var wanted = new HashSet<Guid>(query.FolderIds);
            return folders.Where(f => wanted.Contains(f.Id)).Cast<IMailItemFolder>().ToList();
        }

        return folders.Where(static f => !f.IsHidden && !ExcludedFromBroadSearch.Contains(f.SpecialFolderType)).Cast<IMailItemFolder>().ToList();
    }

    /// <summary>The mail list query for a count: the parts the list can express; the executor applies the rest when the step runs.</summary>
    public static MailListInitializationOptions ToOptions(IReadOnlyList<IMailItemFolder> folders, MailQuery query)
    {
        var filter = (query.Flags & MailStateFlags.Unread) != 0 ? FilterOptionType.Unread
            : (query.Flags & MailStateFlags.Flagged) != 0 ? FilterOptionType.Flagged
            : (query.Flags & MailStateFlags.HasAttachments) != 0 ? FilterOptionType.Files
            : FilterOptionType.All;
        var sender = query.Negated ? string.Empty : query.SenderAddresses.FirstOrDefault() ?? query.SenderDomains.FirstOrDefault() ?? query.SenderWords ?? string.Empty;
        return new MailListInitializationOptions(folders, filter, SortingOptionType.ReceiveDate, false, null, query.Text ?? string.Empty)
        {
            Sender = sender,
            ReceivedAfterUtc = query.From is { } from ? new DateTimeOffset(from.ToUniversalTime()) : null,
            ReceivedBeforeUtc = query.To is { } to ? new DateTimeOffset(to.ToUniversalTime()) : null,
            RequireAttachments = (query.Flags & MailStateFlags.HasAttachments) != 0,
            ReadStatus = (query.Flags & MailStateFlags.Unread) != 0 ? MailReadStatusFilter.Unread : (query.Flags & MailStateFlags.Read) != 0 ? MailReadStatusFilter.Read : MailReadStatusFilter.All,
            RequireFlagged = (query.Flags & MailStateFlags.Flagged) != 0,
            SenderKind = query.SenderKind switch
            {
                SenderKind.Person => MailSenderKind.Person,
                SenderKind.Service => MailSenderKind.Service,
                SenderKind.Newsletter => MailSenderKind.Newsletter,
                _ => MailSenderKind.Any,
            },
            ExcludeDrafts = (query.Flags & MailStateFlags.Draft) == 0,
        };
    }

    private async Task<List<CalendarEventAttendee>> GetAttendeesAsync(CalendarItem item)
    {
        try { return await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? []; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the attendees of an event for the assistant.");
            return [];
        }
    }

    private static HashSet<string> OwnAddresses(IReadOnlyList<MailAccount> accounts)
        => new(accounts.SelectMany(static a => new[] { a.Address, a.AuthenticationAddress }).Where(static a => !string.IsNullOrWhiteSpace(a)).Select(static a => a.Trim()), StringComparer.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? address.Trim().ToLowerInvariant() : address[(at + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>Any word of <paramref name="query"/> starts a word of a name or the local part of an address (case and accents ignored).</summary>
    private static bool StartsWithAnyWord(string query, string? name, string? address)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var targets = Fold(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!string.IsNullOrWhiteSpace(address))
        {
            var at = address.IndexOf('@');
            targets.AddRange(Fold(at < 0 ? address : address[..at]).Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
        }

        return words.Any(w => targets.Any(t => t.StartsWith(w, StringComparison.Ordinal)));
    }

    private static bool ContainsFolded(string query, params string?[] values)
    {
        var folded = Fold(query);
        if (folded.Length == 0) return true;
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = string.Join(' ', values.Select(Fold));
        return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    private static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) || c == '@' || c == '.' ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString().Trim();
    }
}
