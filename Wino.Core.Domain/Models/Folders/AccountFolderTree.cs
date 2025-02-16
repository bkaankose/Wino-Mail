using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Folders;

/// <summary>
/// Grouped folder information for the menu for given account.
/// </summary>
public class AccountFolderTree
{
    public MailAccount Account { get; }
    public List<IMailItemFolder> Folders { get; set; } = new List<IMailItemFolder>();

    public AccountFolderTree(MailAccount account)
    {
        Account = account;
    }

    public bool HasSpecialTypeFolder(SpecialFolderType type)
    {
        foreach (var folderStructure in Folders)
        {
            bool hasSpecialFolder = folderStructure.ContainsSpecialFolderType(type);

            if (hasSpecialFolder)
                return true;
        }

        return false;
    }
}
