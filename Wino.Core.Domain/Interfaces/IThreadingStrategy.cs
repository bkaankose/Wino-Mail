using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces
{
    public interface IThreadingStrategy
    {
        /// <summary>
        /// Attach thread mails to the list.
        /// </summary>
        /// <param name="items">Original mails.</param>
        /// <returns>Original mails with thread mails.</returns>
        Task<List<IMailItem>> ThreadItemsAsync(List<MailCopy> items, IMailItemFolder threadingForFolder);
        bool ShouldThreadWithItem(IMailItem originalItem, IMailItem targetItem);
    }
}
