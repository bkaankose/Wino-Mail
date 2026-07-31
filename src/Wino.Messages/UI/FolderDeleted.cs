using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record FolderDeleted(MailItemFolder MailItemFolder) : UIMessageBase<FolderDeleted>;
