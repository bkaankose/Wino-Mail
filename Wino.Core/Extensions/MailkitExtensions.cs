using System;
using MailKit;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Extensions
{
    public static class MailkitExtensions
    {
        public static MailItemFolder GetLocalFolder(this IMailFolder mailkitMailFolder)
        {
            return new MailItemFolder()
            {
                Id = Guid.NewGuid(),
                FolderName = mailkitMailFolder.Name,
                RemoteFolderId = mailkitMailFolder.FullName,
                ParentRemoteFolderId = mailkitMailFolder.ParentFolder?.FullName,
                SpecialFolderType = Domain.Enums.SpecialFolderType.Other
            };
        }
    }
}
