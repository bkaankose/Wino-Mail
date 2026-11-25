using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// Exchange transports address the same server objects under different ids: EWS uses opaque ids,
/// MAPI uses "mapi:" ids and ActiveSync uses "eas:" ids. When an account changes transport, the rows
/// stored under the other scheme would be dropped and imported again, losing what only the client
/// knows about them (contact favourites, list memberships and pictures; a task's My Day date and
/// local identity). This matches such rows to the incoming ones by content so they can be re-keyed
/// in place. Translating the ids themselves would need a server round trip per item.
/// </summary>
public static class ExchangeTransportRekey
{
    private const string MapiIdPrefix = "mapi:";

    /// <summary>Whether the two remote ids come from different transports.</summary>
    public static bool IsOtherTransport(string storedRemoteId, string incomingRemoteId)
        => !string.IsNullOrWhiteSpace(storedRemoteId) &&
           !string.IsNullOrWhiteSpace(incomingRemoteId) &&
           IdScheme(storedRemoteId) != IdScheme(incomingRemoteId);

    /// <summary>
    /// Stored contacts to re-key, by local id, with the remote id they take. A contact is matched by
    /// its primary e-mail address and display name, and only when that pair is unique on both sides.
    /// </summary>
    public static Dictionary<Guid, string> MatchContacts(IEnumerable<AccountContact> stored, IEnumerable<AccountContact> incoming)
        => Match(
            stored.Where(contact => contact.PendingMutation == ContactPendingMutation.None),
            incoming,
            contact => contact.Id,
            contact => contact.RemoteId,
            ContactKey);

    /// <summary>
    /// Stored tasks to re-key, by local id, with the remote id they take. A task is matched by its
    /// title and due date, and only when that pair is unique on both sides.
    /// </summary>
    public static Dictionary<Guid, string> MatchTasks(IEnumerable<AccountTask> stored, IEnumerable<AccountTask> incoming)
        => Match(
            stored.Where(task => task.PendingMutation == TaskPendingMutation.None),
            incoming,
            task => task.Id,
            task => task.RemoteId,
            TaskKey);

    /// <summary>
    /// Stored task lists to re-key, by local id, with the folder id they take. The default list takes the default
    /// folder; the others are matched by title, only when the title is unique on both sides. A list left unmatched is
    /// dropped by the topology reconciliation and its folder imported as a new list.
    /// </summary>
    public static Dictionary<Guid, string> MatchTaskLists(IEnumerable<AccountTaskList> stored, IReadOnlyList<(string RemoteId, string Title, bool IsDefault)> incoming)
    {
        var result = new Dictionary<Guid, string>();
        if (incoming.Count == 0)
            return result;

        var storedLists = stored.ToList();
        var sample = incoming[0].RemoteId;
        var claimed = storedLists.Select(list => list.RemoteId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var candidates = storedLists
            .Where(list => list.PendingMutation == TaskPendingMutation.None && IsOtherTransport(list.RemoteId, sample))
            .ToList();
        var open = incoming.Where(folder => !claimed.Contains(folder.RemoteId)).ToList();

        var incomingDefault = open.FindIndex(folder => folder.IsDefault);
        if (candidates.FirstOrDefault(list => list.IsDefault) is { } storedDefault && incomingDefault >= 0)
        {
            result[storedDefault.Id] = open[incomingDefault].RemoteId;
            candidates.Remove(storedDefault);
            open.RemoveAll(folder => folder.IsDefault);
        }

        static string TitleKey(string title) => title?.Trim().ToLowerInvariant() ?? string.Empty;

        var openByTitle = open
            .Where(folder => !folder.IsDefault && TitleKey(folder.Title).Length > 0)
            .GroupBy(folder => TitleKey(folder.Title), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().RemoteId, StringComparer.Ordinal);

        foreach (var group in candidates
                     .Where(list => !list.IsDefault && TitleKey(list.Title).Length > 0)
                     .GroupBy(list => TitleKey(list.Title), StringComparer.Ordinal)
                     .Where(group => group.Count() == 1))
        {
            if (openByTitle.TryGetValue(group.Key, out var remoteId))
                result[group.First().Id] = remoteId;
        }

        return result;
    }

    /// <summary>
    /// The transport whose id scheme a stored folder row carries. ActiveSync rows are marked by their folder type,
    /// because ActiveSync server ids are short opaque strings without a prefix.
    /// </summary>
    public static ExchangeTransport FolderTransport(MailItemFolder folder)
        => folder.EasFolderType > 0 ? ExchangeTransport.ActiveSync
            : folder.RemoteFolderId?.StartsWith(MapiIdPrefix, StringComparison.Ordinal) == true ? ExchangeTransport.MapiHttp
            : ExchangeTransport.Ews;

    /// <summary>
    /// Stored folder rows to re-key, by local id, with the remote folder id they take on <paramref name="transport"/>,
    /// for rows whose ids have no documented relationship to the incoming ones: rows ActiveSync stored when the account
    /// now uses EWS or MAPI, and rows EWS or MAPI stored when it now uses ActiveSync (ActiveSync server ids are opaque).
    /// EWS and MAPI rows are re-keyed between each other by their folder entry id instead. Inbox, Sent Items, Drafts and
    /// Deleted Items match by their server role first; every other folder matches by its path of names from the root,
    /// only when that path is unique on both sides. Re-keying keeps the row's id, so its settings (pinned, hidden,
    /// synchronization, order, colors, a special type the user assigned) survive the switch. Rows the same transport
    /// stored keep their ids and are never candidates, so a steady-state pass returns nothing.
    /// </summary>
    public static Dictionary<Guid, string> MatchFolders(IReadOnlyList<MailItemFolder> stored, IReadOnlyList<ExchangeFolderCandidate> incoming,
        ExchangeTransport transport)
    {
        var result = new Dictionary<Guid, string>();
        var candidates = stored
            .Where(folder => !string.IsNullOrEmpty(folder.RemoteFolderId) && FolderTransport(folder) != transport &&
                (transport == ExchangeTransport.ActiveSync || FolderTransport(folder) == ExchangeTransport.ActiveSync))
            .ToList();
        if (candidates.Count == 0 || incoming.Count == 0)
            return result;

        var claimed = stored
            .Where(folder => FolderTransport(folder) == transport && !string.IsNullOrEmpty(folder.RemoteFolderId))
            .Select(folder => folder.RemoteFolderId)
            .ToHashSet(StringComparer.Ordinal);
        var open = incoming
            .Where(folder => !string.IsNullOrEmpty(folder.RemoteId) && !claimed.Contains(folder.RemoteId))
            .ToList();

        foreach (var role in ServerRoles)
        {
            var storedRole = candidates.Where(folder => folder.SpecialFolderType == role).ToList();
            var incomingRole = open.Where(folder => folder.SpecialType == role).ToList();
            if (storedRole.Count != 1 || incomingRole.Count != 1)
                continue;

            result[storedRole[0].Id] = incomingRole[0].RemoteId;
            candidates.Remove(storedRole[0]);
            open.Remove(incomingRole[0]);
        }

        var storedById = stored
            .Where(folder => !string.IsNullOrEmpty(folder.RemoteFolderId))
            .GroupBy(folder => folder.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var incomingById = incoming
            .Where(folder => !string.IsNullOrEmpty(folder.RemoteId))
            .GroupBy(folder => folder.RemoteId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var openByPath = open
            .Select(folder => (Folder: folder, Path: PathKey(folder.Name, folder.ParentRemoteId, id => incomingById.TryGetValue(id, out var parent) ? (parent.Name, parent.ParentRemoteId) : null)))
            .Where(entry => entry.Path.Length > 0)
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Folder.RemoteId, StringComparer.Ordinal);

        foreach (var group in candidates
                     .Select(folder => (Folder: folder, Path: PathKey(folder.FolderName, folder.ParentRemoteFolderId, id => storedById.TryGetValue(id, out var parent) ? (parent.FolderName, parent.ParentRemoteFolderId) : null)))
                     .Where(entry => entry.Path.Length > 0)
                     .GroupBy(entry => entry.Path, StringComparer.Ordinal)
                     .Where(group => group.Count() == 1))
        {
            if (openByPath.TryGetValue(group.Key, out var remoteId))
                result[group.First().Folder.Id] = remoteId;
        }

        return result;
    }

    private static readonly SpecialFolderType[] ServerRoles =
        [SpecialFolderType.Inbox, SpecialFolderType.Sent, SpecialFolderType.Draft, SpecialFolderType.Deleted];

    private static string PathKey(string name, string parentId, Func<string, (string Name, string ParentId)?> parentOf)
    {
        var segments = new List<string>();
        var current = name;
        var next = parentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var segment = current?.Trim().ToLowerInvariant() ?? string.Empty;
            if (segment.Length == 0)
                return string.Empty;

            segments.Add(segment);
            if (string.IsNullOrEmpty(next) || !visited.Add(next) || parentOf(next) is not { } parent)
                break;

            current = parent.Name;
            next = parent.ParentId;
        }

        segments.Reverse();
        return string.Join('\u001f', segments);
    }

    private static Dictionary<Guid, string> Match<T>(
        IEnumerable<T> stored,
        IEnumerable<T> incoming,
        Func<T, Guid> localId,
        Func<T, string> remoteId,
        Func<T, string> contentKey)
    {
        var incomingRows = incoming.Where(row => !string.IsNullOrWhiteSpace(remoteId(row))).ToList();
        var incomingIds = incomingRows.Select(remoteId).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<Guid, string>();

        if (incomingRows.Count == 0)
            return result;

        // Only rows the other transport stored are candidates; a row this transport already knows
        // keeps its id, and an incoming row that is already stored is not offered twice.
        var sample = remoteId(incomingRows[0]);
        var storedRows = stored
            .Where(row => IsOtherTransport(remoteId(row), sample) && !incomingIds.Contains(remoteId(row)))
            .ToList();

        if (storedRows.Count == 0)
            return result;

        var storedIds = stored.Select(remoteId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var incomingByKey = incomingRows
            .Where(row => !storedIds.Contains(remoteId(row)))
            .Select(row => (Row: row, Key: contentKey(row)))
            .Where(entry => entry.Key is not null)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.Ordinal);

        foreach (var group in storedRows
                     .Select(row => (Row: row, Key: contentKey(row)))
                     .Where(entry => entry.Key is not null)
                     .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() == 1))
        {
            if (incomingByKey.TryGetValue(group.Key, out var match))
                result[localId(group.First().Row)] = remoteId(match);
        }

        return result;
    }

    private static string IdScheme(string remoteId)
        => remoteId.StartsWith(MapiIdPrefix, StringComparison.Ordinal) ? "mapi"
            : remoteId.StartsWith("eas:", StringComparison.Ordinal) ? "eas" : "ews";

    private static string ContactKey(AccountContact contact)
    {
        var address = contact.PrimaryEmailAddress?.Trim().ToLowerInvariant() ?? string.Empty;
        var name = contact.DisplayName?.Trim().ToLowerInvariant() ?? string.Empty;

        return address.Length == 0 && name.Length == 0 ? null : address + "\n" + name;
    }

    private static string TaskKey(AccountTask task)
    {
        var title = task.Title?.Trim().ToLowerInvariant() ?? string.Empty;

        return title.Length == 0
            ? null
            : title + "\n" + (task.DueDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);
    }
}

/// <summary>A remote mail folder as one transport reports it, offered to <see cref="ExchangeTransportRekey.MatchFolders"/>.</summary>
public readonly record struct ExchangeFolderCandidate(string RemoteId, string ParentRemoteId, string Name, SpecialFolderType SpecialType);
