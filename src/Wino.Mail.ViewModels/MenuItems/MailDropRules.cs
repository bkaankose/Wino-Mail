using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.ViewModels.MenuItems;

/// <summary>
/// Which dragged mails a drop on a folder moves, shared by every head. A merged folder moves each mail into the folder
/// of its own account. A folder of one account also takes mail of other accounts, which is moved across accounts,
/// unless it is a Drafts folder, the mail is a draft, or the account is known not to receive moved mail.
/// </summary>
public static class MailDropRules
{
    public static List<MailCopy> MailsFor(IBaseFolderMenuItem folder, IEnumerable<MailCopy> mails, Func<Guid, bool> mayReceiveMovedMail)
    {
        if (folder == null || mails == null)
            return [];

        var mailList = mails.Where(mail => mail?.AssignedAccount != null).ToList();
        var handlingFolders = folder.HandlingFolders?.Where(handling => handling != null).ToList() ?? [];
        var accountIds = handlingFolders.Select(handling => handling.MailAccountId).ToHashSet();

        var sameAccountMails = mailList.Where(mail => accountIds.Contains(mail.AssignedAccount.Id)).ToList();

        if (folder is IMergedAccountFolderMenuItem
            || handlingFolders.Count != 1
            || handlingFolders[0].SpecialFolderType == SpecialFolderType.Draft
            || mayReceiveMovedMail?.Invoke(handlingFolders[0].MailAccountId) == false)
        {
            return sameAccountMails;
        }

        var crossAccountMails = mailList.Where(mail => !accountIds.Contains(mail.AssignedAccount.Id) && !mail.IsDraft && !mail.IsLocalDraft);

        return sameAccountMails.Concat(crossAccountMails).ToList();
    }

    public static bool CanDropOn(IBaseFolderMenuItem folder, bool isSelectedFolder, IEnumerable<MailCopy> mails, Func<Guid, bool> mayReceiveMovedMail)
        => folder != null && !isSelectedFolder && folder.IsMoveTarget && MailsFor(folder, mails, mayReceiveMovedMail).Count > 0;
}
