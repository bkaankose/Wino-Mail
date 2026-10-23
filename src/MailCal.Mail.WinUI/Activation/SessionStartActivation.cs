using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using WinRT;

namespace MailCal.Mail.WinUI.Activation;

/// <summary>
/// Starts Mail-Cal a few seconds after logon through a <see cref="SystemTriggerType.SessionConnected"/>
/// background task. Explorer launches startup apps one after another, so the packaged StartupTask
/// only runs 25-50 s after logon on a busy machine; the background task broker does not wait for
/// that queue. Windows activates <see cref="SessionStartComServer"/> out of process with the
/// manifest's <c>--mailcal-session-start</c> argument, and that launch is routed like a StartupTask launch.
/// The StartupTask stays the user's startup switch: the trigger follows its state and is removed
/// when the user disables startup.
/// </summary>
internal static class SessionStartActivation
{
    public const string LaunchArgument = "--mailcal-session-start";

    private const string TaskName = "MailCalSessionConnected";
    // Registered by builds from before the Mail-Cal rename. It activates the same COM class, so it is removed.
    private const string LegacyTaskName = "WinoSessionConnected";
    private const string StartupTaskId = "MailCalStartupId";

    /// <summary>True when Windows launched this process for the session-start background task.</summary>
    public static bool IsCurrentProcess { get; } = Matches(Environment.GetCommandLineArgs().Skip(1));

    public static bool Matches(string? launchArguments)
        => !string.IsNullOrWhiteSpace(launchArguments) &&
           Matches(launchArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool Matches(System.Collections.Generic.IEnumerable<string> arguments)
        => arguments.Contains(LaunchArgument, StringComparer.Ordinal);

    /// <summary>
    /// Whether the user allows Mail-Cal to start with Windows. Reads the StartupTask state, which
    /// Settings > Apps > Startup and Task Manager control. Unreadable state counts as disabled so a
    /// failure never starts the application against the user's choice.
    /// </summary>
    public static async Task<bool> IsStartupEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The startup task state could not be read. Treating startup as disabled.");
            return false;
        }
    }

    /// <summary>
    /// Registers the trigger when startup is enabled and removes it otherwise. Runs after every
    /// launch and after the startup toggle, so existing installations pick it up on their next run.
    /// </summary>
    public static async Task SyncRegistrationAsync()
    {
        try
        {
            RemoveTasks(LegacyTaskName);

            if (!await IsStartupEnabledAsync())
            {
                Unregister();
                return;
            }

            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access is BackgroundAccessStatus.DeniedBySystemPolicy or BackgroundAccessStatus.DeniedByUser)
            {
                Log.Information("Background activity is {Access}. The session-start trigger is not registered.", access);
                return;
            }

            if (BackgroundTaskRegistration.AllTasks.Values.Any(task => task.Name == TaskName))
                return;

            var builder = new Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder { Name = TaskName };
            builder.SetTrigger(new SystemTrigger(SystemTriggerType.SessionConnected, oneShot: false));
            builder.SetTaskEntryPointClsid(MailCal.NotificationHost.Contracts.ReleaseIdentity.Current.StartupTaskActivatorId);
            builder.Register();

            Log.Information("Registered the session-start background task.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The session-start background task registration could not be updated.");
        }
    }

    public static void Unregister()
    {
        RemoveTasks(TaskName);
        RemoveTasks(LegacyTaskName);
    }

    private static void RemoveTasks(string name)
    {
        try
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(task => task.Name == name).ToList())
            {
                task.Unregister(cancelTask: true);
                Log.Information("Removed the session-start background task.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The session-start background task could not be removed.");
        }
    }
}

/// <summary>
/// The out-of-process COM server for the session-start task. The class object lives on its own
/// MTA thread so the broker's calls never land on the XAML thread, and it is registered before
/// <c>Application.Start</c> because COM is waiting for it when it launched this process. Every
/// primary instance registers it, so a trigger that fires while Mail-Cal already runs is answered
/// in process instead of starting a second process.
/// </summary>
internal sealed partial class SessionStartComServer
{
    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleUse = 0x1;
    private const int ClassENoAggregation = unchecked((int)0x80040110);
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static SessionStartComServer? _current;

    private readonly ManualResetEventSlim _stop = new(false);
    private readonly TaskClassFactory _factory = new();
    private uint _registrationCookie;

    /// <summary>The registration failure, if any, for the application to log once logging exists.</summary>
    public static Exception? StartupFailure { get; private set; }

    public static void TryStart(Guid classId)
    {
        if (_current != null)
            return;

        var server = new SessionStartComServer();
        using var registered = new ManualResetEventSlim(false);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                server.Register(classId);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                registered.Set();
            }

            // The MTA that owns the class object must outlive this method.
            if (failure == null)
                server._stop.Wait();
        })
        {
            IsBackground = true,
            Name = "Mail-Cal session-start COM server"
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        registered.Wait();

        if (failure != null)
        {
            StartupFailure = failure;
            return;
        }

        _current = server;
    }

    private void Register(Guid classId)
    {
        var factoryPointer = ComWrappers.GetOrCreateComInterfaceForObject(_factory, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(
                in classId,
                factoryPointer,
                ClsctxLocalServer,
                RegclsMultipleUse,
                out _registrationCookie));
        }
        finally
        {
            Marshal.Release(factoryPointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(
        in Guid classId,
        nint classFactory,
        uint classContext,
        uint flags,
        out uint registrationCookie);

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, in Guid interfaceId, out nint instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [GeneratedComClass]
    internal sealed partial class TaskClassFactory : IClassFactory
    {
        public int CreateInstance(nint outer, in Guid interfaceId, out nint instance)
        {
            instance = 0;
            if (outer != 0)
                return ClassENoAggregation;

            // IBackgroundTask is a WinRT interface: the broker gets a CsWinRT wrapper, not a plain COM one.
            var task = MarshalInterface<IBackgroundTask>.FromManaged(new SessionStartBackgroundTask());
            try
            {
                return Marshal.QueryInterface(task, in interfaceId, out instance);
            }
            catch
            {
                instance = 0;
                return ENoInterface;
            }
            finally
            {
                Marshal.Release(task);
            }
        }

        public int LockServer(bool lockServer) => 0;
    }

    /// <summary>
    /// The task itself has nothing to do: launching this process was the work. The application
    /// start that runs in parallel decides, like a StartupTask launch, whether to stay in the tray.
    /// </summary>
    internal sealed partial class SessionStartBackgroundTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            Log.Information(
                "Session-start background task {Task} ran (T+{Elapsed:F0} ms).",
                taskInstance.Task.Name,
                Stopwatch.GetElapsedTime(Program.StartupTimestamp).TotalMilliseconds);
        }
    }
}
