using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// Maps an Exchange folder container class (PR_CONTAINER_CLASS, for example "IPF.Note", "IPF.Appointment"
/// or "IPF.Contact") to the <see cref="PublicFolderKind"/> that decides where a remote folder is surfaced.
/// The class is trusted exactly as Outlook does: a contacts folder that was created as a generic folder
/// reports IPF.Note and is shown as a mail folder.
/// </summary>
public static class PublicFolderClassifier
{
    public static PublicFolderKind Classify(string folderClass)
    {
        // A structural parent often has no content class; treat it as a container the user can expand.
        if (string.IsNullOrWhiteSpace(folderClass))
            return PublicFolderKind.Container;

        if (folderClass.StartsWith("IPF.Appointment", StringComparison.OrdinalIgnoreCase))
            return PublicFolderKind.Calendar;

        if (folderClass.StartsWith("IPF.Contact", StringComparison.OrdinalIgnoreCase))
            return PublicFolderKind.Contacts;

        // IPF.Note covers both mail and post folders, which both render in the mail list.
        if (folderClass.StartsWith("IPF.Note", StringComparison.OrdinalIgnoreCase))
            return PublicFolderKind.Mail;

        // Tasks, journal, sticky notes and the like: visible in the tree, no specialized surface.
        return PublicFolderKind.Other;
    }
}
