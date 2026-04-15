using System;
using System.Collections.Generic;
using System.Linq;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Gmail.v1.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Misc;
using Wino.Services;

namespace Wino.Core.Extensions;

public static class GoogleIntegratorExtensions
{
    private static bool TryGetKnownFolderLabelName(string labelName, out string normalizedLabelName)
    {
        normalizedLabelName = string.Empty;

        if (string.IsNullOrEmpty(labelName))
            return false;

        var knownFolderKey = labelName.Replace(ServiceConstants.CATEGORY_PREFIX, string.Empty);

        if (!ServiceConstants.KnownFolderDictionary.ContainsKey(knownFolderKey))
            return false;

        normalizedLabelName = char.ToUpper(knownFolderKey[0]) + knownFolderKey.Substring(1).ToLower();

        return true;
    }

    public static MailItemFolder GetLocalFolder(this Label label, ListLabelsResponse labelsResponse, Guid accountId)
    {
        var folderName = GetFolderName(label.Name);

        var lookupLabelName = GetLookupLabelName(label.Name);

        bool isSpecialFolder = ServiceConstants.KnownFolderDictionary.ContainsKey(lookupLabelName);

        var specialFolderType = isSpecialFolder ? ServiceConstants.KnownFolderDictionary[lookupLabelName] : SpecialFolderType.Other;

        // We used to support FOLDER_HIDE_IDENTIFIER to hide invisible folders.
        // However, a lot of people complained that they don't see their folders after the initial sync
        // without realizing that they are hidden in Gmail settings. Therefore, it makes more sense to ignore Gmail's configuration
        // since Wino allows folder visibility configuration separately.

        // Overridden hidden labels are shown in the UI.
        // Also Gmail does not support folder sync enable/disable options due to history changes.
        // By default all folders will be enabled for synchronization.

        bool isHidden = false;

        bool isChildOfCategoryFolder = label.Name.StartsWith(ServiceConstants.CATEGORY_PREFIX);
        bool isSticky = isSpecialFolder && specialFolderType != SpecialFolderType.Category && !isChildOfCategoryFolder;

        // By default, all special folders update unread count in the UI except Trash.
        bool shouldShowUnreadCount = specialFolderType != SpecialFolderType.Deleted || specialFolderType != SpecialFolderType.Other;

        bool isSystemFolder = label.Type == ServiceConstants.SYSTEM_FOLDER_IDENTIFIER;

        var localFolder = new MailItemFolder()
        {
            TextColorHex = label.Color?.TextColor,
            BackgroundColorHex = label.Color?.BackgroundColor,
            FolderName = folderName,
            RemoteFolderId = label.Id,
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            IsSynchronizationEnabled = true,
            SpecialFolderType = specialFolderType,
            IsSystemFolder = isSystemFolder,
            IsSticky = isSticky,
            IsHidden = isHidden,
            ShowUnreadCount = shouldShowUnreadCount,
        };

        localFolder.ParentRemoteFolderId = isChildOfCategoryFolder ? string.Empty : GetParentFolderRemoteId(label.Name, labelsResponse);

        return localFolder;
    }

    public static bool GetIsDraft(this Message message)
        => message?.LabelIds?.Any(a => a == ServiceConstants.DRAFT_LABEL_ID) ?? false;

    public static bool GetIsUnread(this Message message)
        => message?.LabelIds?.Any(a => a == ServiceConstants.UNREAD_LABEL_ID) ?? false;

    public static bool GetIsFocused(this Message message)
        => message?.LabelIds?.Any(a => a == ServiceConstants.IMPORTANT_LABEL_ID) ?? false;

    public static bool GetIsFlagged(this Message message)
        => message?.LabelIds?.Any(a => a == ServiceConstants.STARRED_LABEL_ID) ?? false;

    private static string GetParentFolderRemoteId(string fullLabelName, ListLabelsResponse labelsResponse)
    {
        if (string.IsNullOrEmpty(fullLabelName)) return string.Empty;

        // Find the last index of '/'
        int lastIndex = fullLabelName.LastIndexOf('/');

        // If '/' not found or it's at the start, return the empty string.
        if (lastIndex <= 0) return string.Empty;

        // Extract the parent label
        var parentLabelName = fullLabelName.Substring(0, lastIndex);

        return labelsResponse.Labels.FirstOrDefault(a => a.Name == parentLabelName)?.Id ?? string.Empty;
    }

    public static string GetLookupLabelName(string fullFolderName)
    {
        var folderName = GetLastFolderName(fullFolderName);

        if (string.IsNullOrEmpty(folderName))
            return string.Empty;

        return folderName.Replace(ServiceConstants.CATEGORY_PREFIX, string.Empty);
    }

    public static string GetFolderName(string fullFolderName)
    {
        var lastPart = GetLastFolderName(fullFolderName);

        if (string.IsNullOrEmpty(lastPart))
            return string.Empty;

        return TryGetKnownFolderLabelName(lastPart, out var normalizedLabelName)
            ? normalizedLabelName
            : lastPart;
    }

    private static string GetLastFolderName(string fullFolderName)
    {
        if (string.IsNullOrEmpty(fullFolderName)) return string.Empty;

        // Folders with "//" at the end has "/" as the name.
        if (fullFolderName.EndsWith(ServiceConstants.FOLDER_SEPERATOR_STRING)) return ServiceConstants.FOLDER_SEPERATOR_STRING;

        string[] parts = fullFolderName.Split(ServiceConstants.FOLDER_SEPERATOR_CHAR);

        return parts[parts.Length - 1];
    }

    public static List<RemoteAccountAlias> GetRemoteAliases(this ListSendAsResponse response)
    {
        return response?.SendAs?.Select(a => new RemoteAccountAlias()
        {
            AliasAddress = a.SendAsEmail,
            IsRootAlias = a.IsDefault.GetValueOrDefault(),
            IsPrimary = a.IsPrimary.GetValueOrDefault(),
            ReplyToAddress = a.ReplyToAddress,
            AliasSenderName = a.DisplayName,
            IsVerified = a.VerificationStatus == "accepted" || a.IsDefault.GetValueOrDefault(),
            Source = AliasSource.ProviderDiscovered,
            SendCapability = a.VerificationStatus == "accepted" || a.IsDefault.GetValueOrDefault()
                ? AliasSendCapability.Confirmed
                : AliasSendCapability.Unknown,
        }).ToList();
    }

    public static AccountCalendar AsCalendar(this CalendarListEntry calendarListEntry, Guid accountId, string fallbackBackgroundColor = null)
    {
        var calendar = new AccountCalendar()
        {
            RemoteCalendarId = calendarListEntry.Id,
            AccountId = accountId,
            Name = calendarListEntry.Summary,
            Id = Guid.NewGuid(),
            TimeZone = calendarListEntry.TimeZone,
            IsPrimary = calendarListEntry.Primary.GetValueOrDefault(),
            IsReadOnly = !string.Equals(calendarListEntry.AccessRole, "owner", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(calendarListEntry.AccessRole, "writer", StringComparison.OrdinalIgnoreCase),
            IsSynchronizationEnabled = true,
        };

        // Bg color must present. Generate one if doesnt exists.
        // Text color is optional. It'll be overriden by UI for readibility.

        calendar.BackgroundColorHex = fallbackBackgroundColor ?? ColorHelpers.GenerateFlatColorHex();
        calendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(calendar.BackgroundColorHex);

        return calendar;
    }

    public static DateTimeOffset? GetEventDateTimeOffset(EventDateTime calendarEvent)
    {
        if (calendarEvent != null)
        {
            if (calendarEvent.DateTimeDateTimeOffset != null)
            {
                return calendarEvent.DateTimeDateTimeOffset.Value;
            }
            else if (calendarEvent.Date != null)
            {
                if (DateTime.TryParse(calendarEvent.Date, out DateTime eventDateTime))
                {
                    // Date-only events are treated as UTC midnight
                    return new DateTimeOffset(eventDateTime, TimeSpan.Zero);
                }
                else
                {
                    throw new Exception("Invalid date format in Google Calendar event date.");
                }
            }
        }

        return null;
    }

    public static DateTime? GetEventLocalDateTime(EventDateTime calendarEvent)
    {
        if (calendarEvent == null)
        {
            return null;
        }

        if (calendarEvent.DateTimeDateTimeOffset != null)
        {
            return DateTime.SpecifyKind(calendarEvent.DateTimeDateTimeOffset.Value.DateTime, DateTimeKind.Unspecified);
        }

        if (calendarEvent.Date != null)
        {
            if (DateTime.TryParse(calendarEvent.Date, out DateTime eventDateTime))
            {
                return DateTime.SpecifyKind(eventDateTime, DateTimeKind.Unspecified);
            }

            throw new Exception("Invalid date format in Google Calendar event date.");
        }

        return null;
    }

    /// <summary>
    /// Extracts the timezone string from EventDateTime.
    /// Returns null for all-day events or if timezone is not specified.
    /// </summary>
    public static string GetEventTimeZone(EventDateTime eventDateTime)
    {
        return eventDateTime?.TimeZone;
    }

    /// <summary>
    /// RRULE, EXRULE, RDATE and EXDATE lines for a recurring event, as specified in RFC5545.
    /// </summary>
    /// <returns>___ separated lines.</returns>
    public static string GetRecurrenceString(this Event calendarEvent)
    {
        if (calendarEvent == null || calendarEvent.Recurrence == null || !calendarEvent.Recurrence.Any())
        {
            return null;
        }

        return string.Join(Constants.CalendarEventRecurrenceRuleSeperator, calendarEvent.Recurrence);
    }
}
