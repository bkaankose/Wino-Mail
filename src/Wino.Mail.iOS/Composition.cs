using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using UIKit;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad;
using Wino.Mail.UIKit.Phone;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Apple;
using Wino.Platform.Apple.Services;
using Wino.Platform.iOS;
using Wino.Services;

namespace Wino.Mail.iOS;

/// <summary>The composition root, mirroring the Mac Composition: shared and core services, the iOS platform, ViewModels, the UIKit layer and the idiom's shell.</summary>
internal static class Composition
{
    /// <summary>The bundle identifier. It names the Keychain services and the container folders, so it never changes.</summary>
    public const string BundleIdentifier = "com.winomail.macos";

    /// <summary>Builds the container on the main thread (the platform registration reads the device idiom).</summary>
    public static ServiceProvider Create(IDispatcher dispatcher, AppleNotificationResponseHandler notificationResponses)
    {
        // Paths and protection precede database or credential service construction.
        var bootstrap = IOSPlatformBootstrap.Create(new AppleApplicationIdentity(BundleIdentifier));
        var isPad = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad;

        // Sign-in sessions present from the top-most controller of the scene window, resolved once the container exists.
        ServiceProvider? provider = null;
        UIViewController? Presenter() => provider?.GetService<UIKitAuthenticationPresenter>()?.Presenter();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });
        services.RegisterSharedServices();
        services.RegisterCoreServices();
        services.AddIOSPlatformServices(bootstrap, Presenter);

        services.RegisterWinoViewModels();
        services.RegisterUIKitServices(dispatcher);
        services.RegisterUIKitViews();
        if (isPad) services.AddPadShell();
        else services.AddPhoneShell();

        // Head lifecycle hooks, run in order after the runtime started (ApplicationLifecycleCoordinator).
        services.AddSingleton<IApplicationLifecycleHook>(container => new NotificationsLifecycleHook(container, notificationResponses));
        services.AddSingleton<IApplicationLifecycleHook>(container => new ActivationLifecycleHook(container));
        services.AddSingleton<IApplicationLifecycleHook, BackgroundRefreshLifecycleHook>();
#if WINO_APPSTORE
        services.AddSingleton<IApplicationLifecycleHook>(container => new AppStoreLifecycleHook(container));
#endif
#if DEBUG
        services.AddSingleton<IApplicationLifecycleHook>(container => new DebugBridgeLifecycleHook(container, dispatcher));
#endif

        provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return provider;
    }
}
