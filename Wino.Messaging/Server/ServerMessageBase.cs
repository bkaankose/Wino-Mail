using Wino.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    public record ServerMessageBase<T> : IServerMessage { }
}
