using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record FolderSynchronizationEnabled(MailItemFolder MailItemFolder) : UIMessageBase<FolderSynchronizationEnabled>;