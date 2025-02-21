using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Server.MessageHandlers;

namespace Wino.Server.Core;

public class ServerMessageHandlerFactory : IServerMessageHandlerFactory
{
    public ServerMessageHandlerBase GetHandler(string typeName)
    {
        return typeName switch
        {
            nameof(NewMailSynchronizationRequested) => App.Current.Services.GetService<MailSynchronizationRequestHandler>(),
            nameof(NewCalendarSynchronizationRequested) => App.Current.Services.GetService<CalendarSynchronizationRequestHandler>(),
            nameof(ServerRequestPackage) => App.Current.Services.GetService<UserActionRequestHandler>(),
            nameof(DownloadMissingMessageRequested) => App.Current.Services.GetService<SingleMimeDownloadHandler>(),
            nameof(AuthorizationRequested) => App.Current.Services.GetService<AuthenticationHandler>(),
            nameof(SynchronizationExistenceCheckRequest) => App.Current.Services.GetService<SyncExistenceHandler>(),
            nameof(ServerTerminationModeChanged) => App.Current.Services.GetService<ServerTerminationModeHandler>(),
            nameof(TerminateServerRequested) => App.Current.Services.GetService<TerminateServerRequestHandler>(),
            nameof(ImapConnectivityTestRequested) => App.Current.Services.GetService<ImapConnectivityTestHandler>(),
            nameof(KillAccountSynchronizerRequested) => App.Current.Services.GetService<KillAccountSynchronizerHandler>(),
            nameof(OnlineSearchRequested) => App.Current.Services.GetService<OnlineSearchRequestHandler>(),
            _ => throw new Exception($"Server handler for {typeName} is not registered."),
        };
    }

    public void Setup(IServiceCollection serviceCollection)
    {
        // Register all known handlers.

        serviceCollection.AddTransient<MailSynchronizationRequestHandler>();
        serviceCollection.AddTransient<CalendarSynchronizationRequestHandler>();
        serviceCollection.AddTransient<UserActionRequestHandler>();
        serviceCollection.AddTransient<SingleMimeDownloadHandler>();
        serviceCollection.AddTransient<AuthenticationHandler>();
        serviceCollection.AddTransient<SyncExistenceHandler>();
        serviceCollection.AddTransient<ServerTerminationModeHandler>();
        serviceCollection.AddTransient<TerminateServerRequestHandler>();
        serviceCollection.AddTransient<ImapConnectivityTestHandler>();
        serviceCollection.AddTransient<KillAccountSynchronizerHandler>();
        serviceCollection.AddTransient<OnlineSearchRequestHandler>();
    }
}
