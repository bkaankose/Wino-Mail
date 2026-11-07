#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Services.Voice;

/// <summary>
/// Derives the brain section of one mailbox from local data. Only provider ids (remote folder, calendar and list ids,
/// IMAP paths) and names leave the device; other people appear as name and address hash. The section is ordered
/// deterministically so an unchanged mailbox produces the same hash and is not pushed again.
/// </summary>
public sealed class VoiceBrainSectionBuilder(
    IFolderService folderService,
    ICalendarService calendarService,
    ITaskService taskService,
    IAccountService accountService,
    IMailCategoryService categoryService,
    IRecipientHistoryService recipientHistory)
{
    private const int PastEventDays = 7;
    private const int FutureEventDays = 30;

    private static readonly HashSet<SpecialFolderType> SkippedFolderTypes =
    [
        SpecialFolderType.Chat,
        SpecialFolderType.Category,
        SpecialFolderType.More,
        SpecialFolderType.Unread
    ];

    public async Task<BrainSection?> BuildAsync(MailAccount account, CancellationToken cancellationToken)
    {
        if (VoiceProviderKinds.ToMailboxKey(account) is not { } mailbox)
            return null;

        var folders = await LoadAsync(() => folderService.GetFoldersAsync(account.Id), "folders").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var isCalendarEnabled = account.IsCalendarAccessGranted && account.IsCalendarAccessEnabled;
        var calendars = isCalendarEnabled
            ? await LoadAsync(() => calendarService.GetAccountCalendarsAsync(account.Id), "calendars").ConfigureAwait(false)
            : [];
        cancellationToken.ThrowIfCancellationRequested();

        var isTasksEnabled = account.IsTaskAccessGranted && account.IsTaskAccessEnabled;
        var taskLists = isTasksEnabled
            ? await LoadAsync(() => taskService.GetTaskListsAsync(account.Id), "task lists").ConfigureAwait(false)
            : [];

        var aliases = await LoadAsync(() => accountService.GetAccountAliasesAsync(account.Id), "aliases").ConfigureAwait(false);
        var categories = await LoadAsync(() => categoryService.GetCategoriesAsync(account.Id), "categories").ConfigureAwait(false);
        var correspondents = await LoadAsync(() => recipientHistory.GetMostFrequentAsync(account.Id, BrainSection.MaxContacts * 3), "contacts").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var events = await BuildEventsAsync(calendars, cancellationToken).ConfigureAwait(false);

        var brainFolders = BuildFolders(folders);
        var capabilities = new List<string>();
        if (!folders.Any(static folder => folder.SpecialFolderType == SpecialFolderType.Archive))
            capabilities.Add(BrainSection.CapabilityNoArchive);

        var section = new BrainSection(
            mailbox,
            string.IsNullOrWhiteSpace(account.SenderName) ? null : account.SenderName.Trim(),
            isCalendarEnabled,
            isTasksEnabled,
            brainFolders,
            calendars
                .Where(static calendar => !string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
                .OrderByDescending(static calendar => calendar.IsPrimary)
                .ThenBy(static calendar => calendar.Name, StringComparer.OrdinalIgnoreCase)
                .Take(BrainSection.MaxCalendars)
                .Select(static calendar => new BrainCalendar(calendar.RemoteCalendarId, calendar.Name ?? string.Empty, calendar.IsPrimary, calendar.IsReadOnly))
                .ToList(),
            taskLists
                .Where(static list => !string.IsNullOrWhiteSpace(list.RemoteId))
                .OrderByDescending(static list => list.IsDefault)
                .ThenBy(static list => list.Title, StringComparer.OrdinalIgnoreCase)
                .Take(BrainSection.MaxTaskLists)
                .Select(static list => new BrainTaskList(list.RemoteId, list.Title ?? string.Empty, list.IsDefault))
                .ToList(),
            aliases
                .Select(static alias => alias.AliasAddress)
                .Where(address => !string.IsNullOrWhiteSpace(address) && !string.Equals(BrainMailboxKey.Normalize(address), mailbox.Address, StringComparison.Ordinal))
                .Select(static address => BrainMailboxKey.Normalize(address))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            categories
                .Select(static category => category.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            BuildContacts(correspondents, account),
            events,
            capabilities);

        return Trim(section);
    }

    /// <summary>
    /// The SHA-256 of what the section says, not how it is ordered: folders, calendars and lists as sets, contacts as
    /// members (a change of rank is no change), and only events that have not ended (an event that ages out is no
    /// change). A section is pushed only when this differs from the last pushed value.
    /// </summary>
    public static string ValueHash(BrainSection section, DateTimeOffset now)
    {
        var text = new StringBuilder();
        void Line(params object?[] parts) => text.Append(string.Join('\u001f', parts.Select(static part => part?.ToString() ?? string.Empty))).Append('\n');

        Line("m", section.Mailbox.Provider, BrainMailboxKey.Normalize(section.Mailbox.Address), section.SenderName, section.IsCalendarEnabled, section.IsTasksEnabled);
        foreach (var folder in section.Folders.OrderBy(static folder => folder.RemoteId, StringComparer.Ordinal))
            Line("f", folder.RemoteId, folder.Name, folder.Path, folder.SpecialType);
        foreach (var calendar in section.Calendars.OrderBy(static calendar => calendar.RemoteId, StringComparer.Ordinal))
            Line("c", calendar.RemoteId, calendar.Name, calendar.IsPrimary, calendar.IsReadOnly);
        foreach (var list in section.TaskLists.OrderBy(static list => list.RemoteId, StringComparer.Ordinal))
            Line("l", list.RemoteId, list.Name, list.IsDefault);
        foreach (var address in section.SendAsAddresses.Order(StringComparer.OrdinalIgnoreCase))
            Line("s", address.ToLowerInvariant());
        foreach (var category in section.Categories.Order(StringComparer.Ordinal))
            Line("g", category);
        foreach (var contact in section.TopContacts.OrderBy(static contact => contact.AddressHash, StringComparer.Ordinal))
            Line("p", contact.AddressHash, contact.Name);
        foreach (var ev in section.Events.Where(ev => ev.End > now).OrderBy(static ev => ev.Start).ThenBy(static ev => ev.Title, StringComparer.Ordinal))
            Line("e", ev.Start.ToUnixTimeSeconds(), ev.End.ToUnixTimeSeconds(), ev.IsAllDay, ev.Title, ev.CalendarRemoteId, ev.Recurrence);
        foreach (var capability in section.Capabilities.Order(StringComparer.Ordinal))
            Line("x", capability);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static byte[] Serialize(BrainSection section)
        => JsonSerializer.SerializeToUtf8Bytes(section, WinoAccountApiJsonContext.Default.BrainSection);

    /// <summary>Keeps the section within the server's size limit: events go first, then contacts, then custom folders.</summary>
    private static BrainSection Trim(BrainSection section)
    {
        while (Serialize(section).Length > BrainSection.MaxBytes)
        {
            if (section.Events.Count > 0)
            {
                section.Events.RemoveAt(section.Events.Count - 1);
                continue;
            }

            if (section.TopContacts.Count > 0)
            {
                section.TopContacts.RemoveAt(section.TopContacts.Count - 1);
                continue;
            }

            var custom = section.Folders.FindLastIndex(static folder => folder.SpecialType is SpecialFolderKind.None or SpecialFolderKind.Other);
            if (custom < 0)
                break;

            section.Folders.RemoveAt(custom);
        }

        return section;
    }

    private static List<BrainFolder> BuildFolders(List<MailItemFolder> folders)
    {
        var byRemoteId = folders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .GroupBy(static folder => folder.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        string PathOf(MailItemFolder folder)
        {
            var names = new List<string> { Name(folder) };
            var seen = new HashSet<string>(StringComparer.Ordinal) { folder.RemoteFolderId };
            var parentId = folder.ParentRemoteFolderId;
            while (!string.IsNullOrWhiteSpace(parentId) && seen.Add(parentId) && byRemoteId.TryGetValue(parentId, out var parent))
            {
                names.Insert(0, Name(parent));
                parentId = parent.ParentRemoteFolderId;
            }

            return string.Join("/", names);
        }

        return byRemoteId.Values
            .Where(static folder => !folder.IsHidden && !SkippedFolderTypes.Contains(folder.SpecialFolderType))
            .Select(folder => new BrainFolder(folder.RemoteFolderId, Name(folder), PathOf(folder), ToKind(folder.SpecialFolderType)))
            .OrderBy(static folder => folder.SpecialType is SpecialFolderKind.None or SpecialFolderKind.Other ? 1 : 0)
            .ThenBy(static folder => folder.SpecialType)
            .ThenBy(static folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static folder => folder.RemoteId, StringComparer.Ordinal)
            .Take(BrainSection.MaxFolders)
            .ToList();
    }

    private static string Name(MailItemFolder folder)
        => (string.IsNullOrWhiteSpace(folder.DisplayName) ? folder.FolderName : folder.DisplayName)?.Trim() ?? string.Empty;

    private static List<BrainContact> BuildContacts(List<RecipientHistory> correspondents, MailAccount account)
    {
        var own = BrainMailboxKey.Normalize(account.Address ?? string.Empty);
        return correspondents
            .Where(static row => !string.IsNullOrWhiteSpace(row.DisplayName) && !string.IsNullOrWhiteSpace(row.Address))
            .Where(row => !string.Equals(BrainMailboxKey.Normalize(row.Address), own, StringComparison.Ordinal))
            .Select(static row => new BrainContact(row.DisplayName.Trim(), VoiceBrainHash.Address(row.Address)))
            .DistinctBy(static contact => contact.AddressHash)
            .Take(BrainSection.MaxContacts)
            .ToList();
    }

    /// <summary>
    /// Events from the past week and the next month. A recurring series is listed once, at its first occurrence in the
    /// window, with its rule; items that have no provider id yet (not synchronized) are skipped.
    /// </summary>
    private async Task<List<BrainEvent>> BuildEventsAsync(List<AccountCalendar> calendars, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var window = new TimeRange(now.Date.AddDays(-PastEventDays), now.Date.AddDays(FutureEventDays + 1));
        var result = new List<(DateTime Start, string CalendarId, BrainEvent Event)>();
        var seenSeries = new HashSet<Guid>();

        foreach (var calendar in calendars.Where(static calendar => calendar.IsExtended && !string.IsNullOrWhiteSpace(calendar.RemoteCalendarId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await LoadAsync(() => calendarService.GetCalendarEventsAsync(calendar, window), "events").ConfigureAwait(false);
            var parents = new Dictionary<Guid, CalendarItem?>();

            foreach (var item in items.Where(static item => !item.IsHidden).OrderBy(static item => item.LocalStartDate))
            {
                string? recurrence = null;
                var remoteId = item.RemoteEventId;
                if (item.IsRecurringEvent)
                {
                    var seriesId = item.RecurringCalendarItemId ?? item.Id;
                    if (!seenSeries.Add(seriesId))
                        continue;

                    var parent = item.IsRecurringParent ? item : await GetParentAsync(seriesId, parents).ConfigureAwait(false);
                    recurrence = parent?.Recurrence;
                    if (string.IsNullOrWhiteSpace(remoteId))
                        remoteId = parent?.RemoteEventId;
                }

                if (string.IsNullOrWhiteSpace(remoteId) || string.IsNullOrWhiteSpace(item.Title))
                    continue;

                var start = item.LocalStartDate;
                var end = item.LocalEndDate;
                result.Add((start, calendar.RemoteCalendarId, new BrainEvent(
                    item.Title.Trim(),
                    new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(start)),
                    new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(end)),
                    item.IsAllDayEvent,
                    calendar.RemoteCalendarId,
                    string.IsNullOrWhiteSpace(recurrence) ? null : recurrence.Trim())));
            }
        }

        // Upcoming events matter more to the planner than past ones, so the past ones are dropped first when trimming.
        return result
            .OrderBy(entry => entry.Start < now ? 1 : 0)
            .ThenBy(entry => entry.Start < now ? -entry.Start.Ticks : entry.Start.Ticks)
            .ThenBy(static entry => entry.Event.Title, StringComparer.Ordinal)
            .ThenBy(static entry => entry.CalendarId, StringComparer.Ordinal)
            .Take(BrainSection.MaxEvents)
            .Select(static entry => entry.Event)
            .ToList();
    }

    private async Task<CalendarItem?> GetParentAsync(Guid id, Dictionary<Guid, CalendarItem?> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        CalendarItem? parent = null;
        try
        {
            parent = await calendarService.GetCalendarItemAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read a recurring series for the voice brain.");
        }

        cache[id] = parent;
        return parent;
    }

    private static async Task<List<T>> LoadAsync<T>(Func<Task<List<T>>> load, string what)
    {
        try
        {
            return await load().ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the {What} of a mailbox for the voice brain.", what);
            return [];
        }
    }

    private static SpecialFolderKind ToKind(SpecialFolderType type) => type switch
    {
        SpecialFolderType.Inbox => SpecialFolderKind.Inbox,
        SpecialFolderType.Starred => SpecialFolderKind.Starred,
        SpecialFolderType.Important => SpecialFolderKind.Important,
        SpecialFolderType.Sent => SpecialFolderKind.Sent,
        SpecialFolderType.Draft => SpecialFolderKind.Draft,
        SpecialFolderType.Archive => SpecialFolderKind.Archive,
        SpecialFolderType.Deleted => SpecialFolderKind.Deleted,
        SpecialFolderType.Junk => SpecialFolderKind.Junk,
        SpecialFolderType.Other => SpecialFolderKind.None,
        _ => SpecialFolderKind.Other
    };
}
