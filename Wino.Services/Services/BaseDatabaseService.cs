using CommunityToolkit.Mvvm.Messaging;
using SQLite;
using Wino.Domain.Interfaces;

namespace Wino.Services.Services
{
    public class BaseDatabaseService
    {
        protected IMessenger Messenger => WeakReferenceMessenger.Default;
        protected SQLiteAsyncConnection Connection => _databaseService.Connection;

        private readonly IDatabaseService _databaseService;

        public BaseDatabaseService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IServerMessage
            => Messenger.Send(message);
    }
}
