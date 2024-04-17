using MimeKit;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Models.MailItem
{
    public record NewMailItemPackage(MailCopy Copy, MimeMessage Mime, string AssignedRemoteFolderId);
}
