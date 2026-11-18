using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Messaging.UI;

public record FolderRenamed(MailItemFolder MailItemFolder) : UIMessageBase<FolderRenamed>;
