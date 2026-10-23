using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Models.MailItem;

namespace MailCal.Core.Domain.Interfaces;

public interface IDraftSaveService
{
    Task<bool> SaveAsync(DraftUpdateSnapshot snapshot, MailCopy metadata);
}
