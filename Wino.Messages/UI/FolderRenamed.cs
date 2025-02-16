using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record FolderRenamed(MailItemFolder MailItemFolder) : UIMessageBase<FolderRenamed>;
