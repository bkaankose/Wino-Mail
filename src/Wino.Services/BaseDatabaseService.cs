using CommunityToolkit.Mvvm.Messaging;
using SQLite;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class BaseDatabaseService
{
    protected IMessenger Messenger { get; }
    protected SQLiteAsyncConnection Connection => _databaseService.Connection;

    private readonly IDatabaseService _databaseService;

    public BaseDatabaseService(IDatabaseService databaseService, IMessenger messenger)
    {
        _databaseService = databaseService;
        Messenger = messenger ?? throw new System.ArgumentNullException(nameof(messenger));
    }

    public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage
        => Messenger.Send(message);
}
