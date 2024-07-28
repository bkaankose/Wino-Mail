using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;

namespace Wino.BackgroundTasks
{
    public sealed class SessionConnectedTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            var def = taskInstance.GetDeferral();

            // Run server on session connected by launching the Full Thrust process.
            await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

            def.Complete();
        }
    }
}
