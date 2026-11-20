using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Wino.Core;
using Wino.Core.Providers;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views;
using Wino.Platform.Linux;
using Wino.Providers.Dav.Providers;
using Wino.Providers.Exchange.Providers;
using Wino.Providers.Gmail.Providers;
using Wino.Providers.Imap.Providers;
using Wino.Providers.Outlook.Providers;
using Wino.Services;

namespace Wino.Mail.Linux;

/// <summary>
/// The composition root, in the order of the other heads: shared and core services, providers, the Linux platform,
/// then the head (<see cref="LinuxViewModelRegistration"/>). Nothing else is registered here: a head service goes to
/// a <c>LinuxViewModelRegistration.&lt;Area&gt;.cs</c> hook, a platform service to Wino.Platform.Linux. Call it on the
/// GTK main thread.
/// </summary>
internal static class LinuxComposition
{
    public static ServiceProvider Create(LinuxPlatformBootstrap bootstrap, Func<MainWindow?> window, LaunchOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });
        services.RegisterSharedServices();
        services.RegisterCoreServices();
        services.AddGmailProvider().AddOutlookProvider().AddImapProvider().AddExchangeProvider().AddDavProvider();
        services.AddLinuxPlatformServices(bootstrap, () => window()?.Window);

        services.RegisterLinuxHead(window, options);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
