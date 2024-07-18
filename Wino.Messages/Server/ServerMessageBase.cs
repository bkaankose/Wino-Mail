using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    public record ServerMessageBase<T> : IServerMessage { }
}
