﻿using System;
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
        private const string DatabaseName = "Wino180.db";

        private bool _isInitialized = false;
        private readonly IApplicationConfiguration _folderConfiguration;

        public SQLiteAsyncConnection Connection { get; private set; }

        public DatabaseService(IApplicationConfiguration folderConfiguration)
        {
            _folderConfiguration = folderConfiguration;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            var publisherCacheFolder = _folderConfiguration.PublisherSharedFolderPath;
            var databaseFileName = Path.Combine(publisherCacheFolder, DatabaseName);

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
                typeof(AccountContact),
                typeof(CustomServerInformation),
                typeof(AccountSignature),
                typeof(MergedInbox),
                typeof(MailAccountPreferences),
                typeof(MailAccountAlias)
                );
        }
    }
}
