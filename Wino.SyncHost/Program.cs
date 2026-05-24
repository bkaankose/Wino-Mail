using System.Threading;
using System.Text;
using Serilog;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace Wino.SyncHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: Wino.Messaging.SyncHost.SyncHostProtocol.RunningMutexName,
            createdNew: out var createdNew);

        if (!createdNew)
            return 0;

        var wpfApplication = new WpfApplication
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var application = new SyncHostApplication(args, () => wpfApplication.Dispatcher.Invoke(wpfApplication.Shutdown));

        try
        {
            application.StartAsync().GetAwaiter().GetResult();
            application.StartTray();
            using var shutdownRegistration = application.ShutdownToken.Register(() => wpfApplication.Dispatcher.Invoke(wpfApplication.Shutdown));
            wpfApplication.Run();
            application.StopAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Wino sync host terminated unexpectedly.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
