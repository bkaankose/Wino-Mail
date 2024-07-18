using Wino.Core.Domain.Models.Folders;

namespace Wino.Messaging.Server
{
    public record FolderSynchronizationEnabled(IMailItemFolder MailItemFolder) : ServerMessageBase<FolderSynchronizationEnabled>;
}
