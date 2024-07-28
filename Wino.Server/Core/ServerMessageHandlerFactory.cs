using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Client.Authorization;
using Wino.Messaging.Server;
using Wino.Server.MessageHandlers;

namespace Wino.Server.Core
{
    public class ServerMessageHandlerFactory : IServerMessageHandlerFactory
    {
        public ServerMessageHandlerBase GetHandler(string typeName)
        {
            return typeName switch
            {
                nameof(NewSynchronizationRequested) => App.Current.Services.GetService<SynchronizationRequestHandler>(),
                nameof(ServerRequestPackage) => App.Current.Services.GetService<UserActionRequestHandler>(),
                nameof(DownloadMissingMessageRequested) => App.Current.Services.GetService<SingleMimeDownloadHandler>(),
                nameof(AuthorizationRequested) => App.Current.Services.GetService<AuthenticationHandler>(),
                nameof(ProtocolAuthorizationCallbackReceived) => App.Current.Services.GetService<ProtocolAuthActivationHandler>(),
                nameof(SynchronizationExistenceCheckRequest) => App.Current.Services.GetService<SyncExistenceHandler>(),
                nameof(ServerTerminationModeChanged) => App.Current.Services.GetService<ServerTerminationModeHandler>(),
                _ => throw new Exception($"Server handler for {typeName} is not registered."),
            };
        }

        public void Setup(IServiceCollection serviceCollection)
        {
            // Register all known handlers.

            serviceCollection.AddTransient<SynchronizationRequestHandler>();
            serviceCollection.AddTransient<UserActionRequestHandler>();
            serviceCollection.AddTransient<SingleMimeDownloadHandler>();
            serviceCollection.AddTransient<AuthenticationHandler>();
            serviceCollection.AddTransient<ProtocolAuthActivationHandler>();
            serviceCollection.AddTransient<SyncExistenceHandler>();
            serviceCollection.AddTransient<ServerTerminationModeHandler>();
        }
    }
}
