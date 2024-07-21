using Wino.Domain.Models.Folders;

namespace Wino.Messaging.Server
{
    public record FolderRenamed(IMailItemFolder MailItemFolder) : ServerMessageBase<FolderRenamed>;
}
