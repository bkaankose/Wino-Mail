using System;
using MailKit;
using Wino.Domain.Entities;
using Wino.Domain.Enums;

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
                SpecialFolderType = SpecialFolderType.Other
            };
        }
    }
}
