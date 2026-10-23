using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Messaging.UI;

public record FolderRenamed(MailItemFolder MailItemFolder) : UIMessageBase<FolderRenamed>;
