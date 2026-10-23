using MailCal.Core.Domain.Models.Folders;

namespace MailCal.Messaging.UI;

public record FolderSynchronizationEnabled(IMailItemFolder MailItemFolder) : UIMessageBase<FolderSynchronizationEnabled>;
