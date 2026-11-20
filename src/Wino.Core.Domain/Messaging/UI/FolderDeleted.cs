using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Messaging.UI;

public record FolderDeleted(MailItemFolder MailItemFolder) : UIMessageBase<FolderDeleted>;
