using Windows.ApplicationModel;
using Wino.NotificationHost.Contracts;

namespace Wino.WidgetHost;

internal static class Program
{
    private const string ComServerArgument = "-RegisterProcessAsComServer";

    [MTAThread]
    private static int Main()
    {
        try
        {
            if (!Environment.CommandLine.Contains(ComServerArgument, StringComparison.OrdinalIgnoreCase))
                return 0;

            WinRT.ComWrappersSupport.InitializeComWrappers();

            var package = Package.Current;
            ReleaseIdentity.Initialize(package.InstalledLocation.Path, package.Id.Name, package.Id.Publisher, package.Id.FamilyName);

            using var provider = new WinoWidgetProvider();
            using var server = new WidgetProviderComServer(ReleaseIdentity.Current.WidgetProviderId, provider);
            provider.WaitUntilIdle();
            return 0;
        }
        catch
        {
            // A widget failure must never surface anywhere; the Board shows its own error card.
            return 1;
        }
    }
}
