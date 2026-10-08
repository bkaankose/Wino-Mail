#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// One mail the server delivered during synchronization, reduced to what the automatic
/// indexing decision needs: its intelligence identity and the folder it landed in.
/// </summary>
public sealed record SynchronizedMailCapture(string RemoteMessageId, string? RemoteFolderId, SpecialFolderType SpecialFolderType);

/// <summary>
/// Decides which newly synchronized mails the "New mail: Automatic" coverage setting submits.
/// The setting lives in the coverage editor next to the included folders, so a new message
/// counts only when it arrives in one of those folders. Without a configured selection the
/// editor shows Inbox, and automatic indexing follows the same default.
/// </summary>
public static class AutomaticIndexingSelection
{
    /// <summary>
    /// Captures a synchronized mail, or null when intelligence would never read it: a draft, a
    /// copy in a folder intelligence excludes, or a mail without a stable remote identity.
    /// </summary>
    public static SynchronizedMailCapture? TryCapture(MailCopy? mail)
    {
        var folder = mail?.AssignedFolder;
        if (mail is null || folder is null || mail.IsDraft)
        {
            return null;
        }

        if (IntelligenceFolderFilter.ExcludedSpecialFolderTypes.Contains(folder.SpecialFolderType))
        {
            return null;
        }

        var remoteMessageId = RemoteMessageIdentity.TryCreate(mail);
        return remoteMessageId is null
            ? null
            : new SynchronizedMailCapture(remoteMessageId, folder.RemoteFolderId, folder.SpecialFolderType);
    }

    /// <summary>
    /// The distinct remote message ids among <paramref name="captured"/> that landed in an
    /// included folder. A message filed in several folders (a Gmail label set) is included when
    /// any of its copies is.
    /// </summary>
    public static IReadOnlyList<string> Select(MailAccountPreferences? preferences, IEnumerable<SynchronizedMailCapture> captured)
    {
        var configured = preferences?.IsIntelligenceFolderSelectionInitialized == true
            ? preferences.SelectedIntelligenceFolderIds
            : null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<string>();
        foreach (var capture in captured)
        {
            var included = configured is null
                ? capture.SpecialFolderType == SpecialFolderType.Inbox
                : !string.IsNullOrWhiteSpace(capture.RemoteFolderId) && configured.Contains(capture.RemoteFolderId);

            if (included && seen.Add(capture.RemoteMessageId))
            {
                selected.Add(capture.RemoteMessageId);
            }
        }

        return selected;
    }
}
