using System;
using System.Linq;
using MailKit;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Extensions
{
    public static class MailkitExtensions
    {
        public static MailItemFolder GetLocalFolder(this IMailFolder mailkitMailFolder)
        {
            bool isAllCapital = mailkitMailFolder.Name?.All(a => char.IsUpper(a)) ?? false;

            return new MailItemFolder()
            {
                Id = Guid.NewGuid(),
                FolderName = isAllCapital ? mailkitMailFolder.Name.OnlyCapitilizeFirstLetter() : mailkitMailFolder.Name,
                RemoteFolderId = mailkitMailFolder.FullName,
                ParentRemoteFolderId = mailkitMailFolder.ParentFolder?.FullName,
                SpecialFolderType = Domain.Enums.SpecialFolderType.Other
            };
        }

        public static string OnlyCapitilizeFirstLetter(this string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            s = s.ToLower();

            char[] a = s.ToCharArray();
            a[0] = char.ToUpper(a[0]);
            return new string(a);
        }
    }
}
