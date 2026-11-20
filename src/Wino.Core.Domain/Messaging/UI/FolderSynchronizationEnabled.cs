using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Messaging.UI;

public record FolderSynchronizationEnabled(IMailItemFolder MailItemFolder) : UIMessageBase<FolderSynchronizationEnabled>;
