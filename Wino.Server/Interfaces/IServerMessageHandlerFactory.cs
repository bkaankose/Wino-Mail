using Microsoft.Extensions.DependencyInjection;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public interface IServerMessageHandlerFactory
    {
        void Setup(IServiceCollection serviceCollection);

        ServerMessageHandlerBase GetHandler(string typeName);
    }
}
