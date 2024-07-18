using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Wino.Server.WinUI
{
    [STAThread]
    static async Task Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        bool isRedirect = await DecideRedirection();
        if (!isRedirect)
        {
            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        return 0;
    }
}
