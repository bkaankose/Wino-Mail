using Wino.Core.Domain.Models.Folders;

namespace Wino.Messaging.UI;

public record FolderSynchronizationEnabled(IMailItemFolder MailItemFolder) : UIMessageBase<FolderSynchronizationEnabled>;
