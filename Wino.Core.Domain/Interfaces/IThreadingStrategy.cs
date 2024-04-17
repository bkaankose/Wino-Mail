using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces
{
    public interface IThreadingStrategy
    {
        Task<List<IMailItem>> ThreadItemsAsync(List<MailCopy> items);
        bool ShouldThreadWithItem(IMailItem originalItem, IMailItem targetItem);
    }
}
