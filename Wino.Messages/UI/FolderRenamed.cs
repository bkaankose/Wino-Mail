using Wino.Core.Domain.Models.Folders;

namespace Wino.Messaging.UI
{
    public record FolderRenamed(IMailItemFolder MailItemFolder) : UIMessageBase<FolderRenamed>;
}
