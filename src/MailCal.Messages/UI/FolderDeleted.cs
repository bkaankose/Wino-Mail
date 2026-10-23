using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Messaging.UI;

public record FolderDeleted(MailItemFolder MailItemFolder) : UIMessageBase<FolderDeleted>;
