using Wino.Domain.Entities;
using Wino.Domain.Models.MailItem;

namespace Wino.Domain.Interfaces
{
    public interface IThreadingStrategy
    {
        /// <summary>
        /// Attach thread mails to the list.
        /// </summary>
        /// <param name="items">Original mails.</param>
        /// <returns>Original mails with thread mails.</returns>
        Task<List<IMailItem>> ThreadItemsAsync(List<MailCopy> items);
        bool ShouldThreadWithItem(IMailItem originalItem, IMailItem targetItem);
    }

    public interface IOutlookThreadingStrategy : IThreadingStrategy { }

    public interface IGmailThreadingStrategy : IThreadingStrategy { }

    public interface IImapThreadStrategy : IThreadingStrategy { }
}
