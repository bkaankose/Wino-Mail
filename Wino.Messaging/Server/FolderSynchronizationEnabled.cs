using Wino.Domain.Models.Folders;

namespace Wino.Messaging.Server
{
    public record FolderSynchronizationEnabled(IMailItemFolder MailItemFolder) : ServerMessageBase<FolderSynchronizationEnabled>;
}
