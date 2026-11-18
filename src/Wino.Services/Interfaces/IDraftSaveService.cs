using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services.Models.MailItem;

namespace Wino.Services.Interfaces;

public interface IDraftSaveService
{
    Task<bool> SaveAsync(DraftUpdateSnapshot snapshot, MailCopy metadata);
}
