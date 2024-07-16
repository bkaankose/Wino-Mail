using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messages.Server
{
    public record MailRemovedMessage(MailCopy RemovedMail) : IServerMessage;
}
