using System;
using System.IO;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services
{
    public interface IDatabaseService : IInitializeAsync
    {
        SQLiteAsyncConnection Connection { get; }
    }

    public class DatabaseService : IDatabaseService
    {
        private string DatabaseName => "Wino172.db";

        private bool _isInitialized = false;
        private readonly IAppInitializerService _appInitializerService;

        public SQLiteAsyncConnection Connection { get; private set; }

        public DatabaseService(IAppInitializerService appInitializerService)
        {
            _appInitializerService = appInitializerService;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            var applicationData = _appInitializerService.GetApplicationDataFolder();
            var databaseFileName = Path.Combine(applicationData, DatabaseName);

            Connection = new SQLiteAsyncConnection(databaseFileName)
            {
                // Enable for debugging sqlite.
                Trace = true,
                Tracer = new Action<string>((t) =>
                {
                    // Debug.WriteLine(t);
                    // Log.Debug(t);
                })
            };


            await CreateTablesAsync();

            _isInitialized = true;
        }

        private async Task CreateTablesAsync()
        {
            await Connection.CreateTablesAsync(CreateFlags.None,
                typeof(MailCopy),
                typeof(MailItemFolder),
                typeof(MailAccount),
                typeof(TokenInformation),
                typeof(AddressInformation),
                typeof(CustomServerInformation),
                typeof(AccountSignature),
                typeof(MergedInbox),
                typeof(MailAccountPreferences)
                );
        }
    }
}
