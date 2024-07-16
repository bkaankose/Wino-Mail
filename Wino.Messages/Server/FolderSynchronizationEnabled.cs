using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Messages.Server
{
    public record FolderSynchronizationEnabled(IMailItemFolder MailItemFolder) : IServerMessage;
}
