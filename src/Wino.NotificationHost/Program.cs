using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost.Contracts;

namespace Wino.NotificationHost;

internal static class Program
{
    private const string AppNotificationActivatedCommandLinePrefix = "----AppNotificationActivated:";
    private static readonly TimeSpan StaleEnvelopeAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumHostLifetime = TimeSpan.FromSeconds(30);

    [STAThread]
    private static int Main()
    {
        // The host only bridges one toast activation to the main application. Never retain a process
        // indefinitely if a COM call stops responding.
        NotificationHostLifetime.Start(MaximumHostLifetime);

        var exitCode = Run();

        // Exit explicitly so no COM apartment teardown or stray foreground thread can keep the host alive.
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static int Run()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            var package = Package.Current;
            ReleaseIdentity.Initialize(package.InstalledLocation.Path, package.Id.Name, package.Id.Publisher, package.Id.FamilyName);

            var localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
            _ = NotificationHostFileStore.CleanupStaleFiles(localCachePath, StaleEnvelopeAge);

            // The main application shows and removes toasts itself. Windows starts the host only as the
            // COM activator of a toast, so any other launch has nothing to do.
            if (!Environment.CommandLine.Contains(AppNotificationActivatedCommandLinePrefix, StringComparison.OrdinalIgnoreCase))
                return 0;

            return RunActivationBridge(localCachePath);
        }
        catch (Exception ex)
        {
            NotificationHostLogger.Write("failed", exception: ex);
            return 1;
        }
    }

    private static int RunActivationBridge(string localCachePath)
    {
        using var invoked = new ManualResetEventSlim();
        Exception? failure = null;
        var handled = 0;
        void HandleActivation(NotificationHostApplication application, string argument, IReadOnlyDictionary<string, string> userInput)
        {
            if (Interlocked.Exchange(ref handled, 1) != 0)
                return;

            try
            {
                ForwardActivation(localCachePath, application, argument, userInput);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                invoked.Set();
            }
        }

        // All four application entries declare this executable as their toast activator, and Windows
        // starts it under the identity of the toast's application. COM rejects registering another application's
        // activator class (CO_E_WRONG_SERVER_IDENTITY), so only the current one is registered.
        var currentAppUserModelId = CurrentAppIdentity.GetAppUserModelId();
        if (!NotificationHostApplicationIds.TryResolveFromAppUserModelId(currentAppUserModelId, out var application))
            throw new InvalidOperationException("Current AUMID is not a Wino application mode identity.");

        using var comServer = new NotificationActivationComServer(
            GetActivatorClassId(application),
            (argument, userInput) => HandleActivation(application, argument, userInput));

        if (!invoked.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Timed out waiting for notification activation arguments.");

        if (failure != null)
            throw failure;

        return 0;
    }

    private static void ForwardActivation(
        string localCachePath,
        NotificationHostApplication application,
        string argument,
        IReadOnlyDictionary<string, string> userInput)
    {
        var activationId = Guid.NewGuid();
        var envelope = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            application,
            argument,
            userInput);

        NotificationHostFileStore.WriteActivationAsync(localCachePath, activationId, envelope)
            .GetAwaiter()
            .GetResult();

        try
        {
            var mainAppUserModelId = $"{Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.Main}";
            _ = PackagedApplicationActivator.Activate(
                mainAppUserModelId,
                NotificationHostLaunchArguments.CreateForwardedActivation(activationId));
            NotificationHostLogger.Write($"forward-activation:{application}", activationId);
        }
        catch
        {
            NotificationHostFileStore.TryDeleteActivation(localCachePath, activationId);
            throw;
        }
    }

    private static Guid GetActivatorClassId(NotificationHostApplication application) => application switch
    {
        NotificationHostApplication.Mail => ReleaseIdentity.Current.NotificationActivatorIds["Mail"],
        NotificationHostApplication.Calendar => ReleaseIdentity.Current.NotificationActivatorIds["Calendar"],
        NotificationHostApplication.People => ReleaseIdentity.Current.NotificationActivatorIds["People"],
        NotificationHostApplication.Tasks => ReleaseIdentity.Current.NotificationActivatorIds["Tasks"],
        _ => throw new ArgumentOutOfRangeException(nameof(application))
    };
}
