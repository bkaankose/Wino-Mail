using MimeKit;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.MailItem;

public record NewMailItemPackage(MailCopy Copy, MimeMessage Mime, string AssignedRemoteFolderId);
