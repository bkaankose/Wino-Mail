using MimeKit;
using Wino.Domain.Entities;

namespace Wino.Domain.Models.MailItem
{
    public record NewMailItemPackage(MailCopy Copy, MimeMessage Mime, string AssignedRemoteFolderId);
}
