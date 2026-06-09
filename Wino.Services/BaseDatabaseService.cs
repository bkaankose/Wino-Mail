using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Messaging;

namespace Wino.Services;

public class BaseDatabaseService
{
    protected SQLiteAsyncConnection Connection => _databaseService.Connection;

    private readonly IDatabaseService _databaseService;

    public BaseDatabaseService(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage
        => UIMessagePublisherProvider.Current.Publish(message);
}
