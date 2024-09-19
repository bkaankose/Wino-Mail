using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.Comparers
{
    public class FolderNameComparer : IComparer<MailItemFolder>
    {
        public int Compare(MailItemFolder x, MailItemFolder y)
        {
            return x.FolderName.CompareTo(y.FolderName);
        }
    }
}
