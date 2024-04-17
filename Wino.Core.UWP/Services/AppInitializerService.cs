using System;
using System.Threading.Tasks;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class AppInitializerService : IAppInitializerService
    {
        private readonly IBackgroundTaskService _backgroundTaskService;

        public AppInitializerService(IBackgroundTaskService backgroundTaskService)
        {
            _backgroundTaskService = backgroundTaskService;
        }

        public string GetApplicationDataFolder() => ApplicationData.Current.GetPublisherCacheFolder("WinoShared").Path;

        // TODO: Pre 1.7.0 for Wino Calendar...
        //public string GetApplicationDataFolder() => ApplicationData.Current.LocalFolder.Path;

        public Task MigrateAsync()
        {
            UnregisterAllBackgroundTasks();

            return Task.CompletedTask;
        }

        #region 1.6.8 -> 1.6.9

        private void UnregisterAllBackgroundTasks()
        {
            _backgroundTaskService.UnregisterAllBackgroundTask();
        }

        #endregion

        #region 1.7.0

        /// <summary>
        /// We decided to use publisher cache folder as a database going forward.
        /// This migration will move the file from application local folder and delete it.
        /// Going forward database will be initialized from publisher cache folder.
        /// </summary>
        private async Task MoveExistingDatabaseToSharedCacheFolderAsync()
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
