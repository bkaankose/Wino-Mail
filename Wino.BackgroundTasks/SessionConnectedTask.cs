using System;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel.Background;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.UWP;
using Wino.Services;

namespace Wino.BackgroundTasks
{
    public sealed class SessionConnectedTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            var def = taskInstance.GetDeferral();

            try
            {
                var services = new ServiceCollection();

                services.RegisterCoreServices();
                services.RegisterCoreUWPServices();

                var providere = services.BuildServiceProvider();

                var backgroundTaskService = providere.GetService<IBackgroundSynchronizer>();
                var dbService = providere.GetService<IDatabaseService>();
                var logInitializer = providere.GetService<ILogInitializer>();

                logInitializer.SetupLogger(ApplicationData.Current.LocalFolder.Path);

                await dbService.InitializeAsync();
                await backgroundTaskService.RunBackgroundSynchronizationAsync(Core.Domain.Enums.BackgroundSynchronizationReason.SessionConnected);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background synchronization failed from background task.");
            }
            finally
            {
                def.Complete();
            }
        }
    }
}
