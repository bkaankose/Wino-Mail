using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;

namespace Wino.Core.Helpers;

/// <summary>
/// Converts queued synchronization requests into user-facing action descriptions.
/// </summary>
public static class SynchronizationActionHelper
{
    public static List<SynchronizationActionItem> CreateActionItems(
        IEnumerable<IRequestBase> requests, Guid accountId, string accountName)
    {
        var items = new List<SynchronizationActionItem>();

        // Group mail action requests by operation
        var mailRequests = requests.OfType<IMailActionRequest>();
        var mailGroups = mailRequests.GroupBy(r => GetMailActionKey(r));

        foreach (var group in mailGroups)
        {
            var description = GetMailActionDescription(group.Key, group.ToList());

            if (description != null)
            {
                items.Add(new SynchronizationActionItem
                {
                    AccountId = accountId,
                    AccountName = accountName,
                    Description = description
                });
            }
        }

        // Handle folder action requests individually
        var folderRequests = requests.OfType<IFolderActionRequest>();
        foreach (var folderRequest in folderRequests)
        {
            var description = GetFolderActionDescription(folderRequest);

            if (description != null)
            {
                items.Add(new SynchronizationActionItem
                {
                    AccountId = accountId,
                    AccountName = accountName,
                    Description = description
                });
            }
        }

        var calendarRequests = requests.OfType<ICalendarActionRequest>();
        foreach (var calendarRequest in calendarRequests)
        {
            var description = GetCalendarActionDescription(calendarRequest);

            if (description != null)
            {
                items.Add(new SynchronizationActionItem
                {
                    AccountId = accountId,
                    AccountName = accountName,
                    Description = description
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Returns a key that differentiates MarkRead vs MarkUnread, Flag vs Unflag, Archive vs Unarchive.
    /// </summary>
    private static string GetMailActionKey(IMailActionRequest request)
    {
        return request switch
        {
            MarkReadRequest r => r.IsRead ? "MarkRead" : "MarkUnread",
            ChangeFlagRequest r => r.IsFlagged ? "SetFlag" : "ClearFlag",
            ChangeJunkStateRequest r => r.IsJunk ? "MarkJunk" : "MarkNotJunk",
            ArchiveRequest r => r.IsArchiving ? "Archive" : "Unarchive",
            _ => request.Operation.ToString()
        };
    }

    private static string GetMailActionDescription(string actionKey, List<IMailActionRequest> requests)
    {
        int count = requests.Count;

        return actionKey switch
        {
            "MarkRead" => string.Format(Translator.SyncAction_MarkingAsRead, count),
            "MarkUnread" => string.Format(Translator.SyncAction_MarkingAsUnread, count),
            "Delete" => string.Format(Translator.SyncAction_Deleting, count),
            "Move" => string.Format(Translator.SyncAction_Moving, count),
            "MarkJunk" => string.Format(Translator.SyncAction_Moving, count),
            "MarkNotJunk" => string.Format(Translator.SyncAction_Moving, count),
            "Archive" => string.Format(Translator.SyncAction_Archiving, count),
            "Unarchive" => string.Format(Translator.SyncAction_Unarchiving, count),
            "SetFlag" => string.Format(Translator.SyncAction_SettingFlag, count),
            "ClearFlag" => string.Format(Translator.SyncAction_ClearingFlag, count),
            "CreateDraft" => Translator.SyncAction_CreatingDraft,
            "Send" => Translator.SyncAction_SendingMail,
            "MoveToFocused" => string.Format(Translator.SyncAction_MovingToFocused, count),
            "AlwaysMoveTo" => string.Format(Translator.SyncAction_Moving, count),
            _ => null
        };
    }

    private static string GetFolderActionDescription(IFolderActionRequest request)
    {
        return request switch
        {
            RenameFolderRequest => Translator.SyncAction_RenamingFolder,
            EmptyFolderRequest => Translator.SyncAction_EmptyingFolder,
            MarkFolderAsReadRequest => Translator.SyncAction_MarkingFolderAsRead,
            DeleteFolderRequest => Translator.FolderOperation_Delete,
            CreateSubFolderRequest => Translator.FolderOperation_CreateSubFolder,
            CreateRootFolderRequest => Translator.AccountContextMenu_CreateFolder,
            _ => null
        };
    }

    private static string GetCalendarActionDescription(ICalendarActionRequest request)
    {
        return request switch
        {
            CreateCalendarEventRequest => Translator.SyncAction_CreatingEvent,
            _ => null
        };
    }
}
