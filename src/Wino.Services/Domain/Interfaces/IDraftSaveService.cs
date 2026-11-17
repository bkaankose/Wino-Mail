using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces;

public interface IDraftSaveService
{
    Task<bool> SaveAsync(DraftUpdateSnapshot snapshot, MailCopy metadata);
}
