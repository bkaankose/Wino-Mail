using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppLifecycle;
using Wino.Core.Activation;
using Wino.Mail.WinUI.Activation;

namespace Wino.Mail.WinUI;

public class Program
{
    private const string SingleInstanceKey = "WinoMailSingleInstance";
    private const string ForceAlternateModeSignalEventName = "Local\\WinoMailForceAlternateMode";
    private const string MailHostRunningMutexName = "Local\\WinoMailMailHostRunning";
    private const int VkControl = 0x11;

    private static bool _forceAlternateModeOnLaunch;
    private static EventWaitHandle? _forceAlternateModeSignalHandle;
    private static Mutex? _mailHostRunningMutex;
    private static PendingBootstrapActivation? _pendingBootstrapActivation;

    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var shouldBootstrapSecondaryEntry = SecondaryEntryBootstrapActivation.ShouldBootstrapToMailHost(activationArgs);

        if (shouldBootstrapSecondaryEntry && !IsMailHostRunning())
        {
            if (SecondaryEntryBootstrapActivation.QueuePendingActivation(activationArgs) &&
                SecondaryEntryBootstrapActivation.LaunchMailHost())
            {
                return 0;
            }

            SecondaryEntryBootstrapActivation.ClearPendingActivation();
        }

        _pendingBootstrapActivation = SecondaryEntryBootstrapActivation.ConsumePendingActivation();
        bool isRedirect = DecideRedirection(activationArgs);

        if (!isRedirect)
        {
            EnsureMailHostRunningMutex();
            StartApplication();
        }

        return 0;
    }

    internal static bool TryConsumePendingBootstrapActivation(out PendingBootstrapActivation activation)
    {
        if (_pendingBootstrapActivation == null)
        {
            activation = null!;
            return false;
        }

        activation = _pendingBootstrapActivation;
        _pendingBootstrapActivation = null;
        return true;
    }

    private static bool DecideRedirection(AppActivationArguments args)
    {
        bool isRedirect = false;
        _forceAlternateModeOnLaunch = args.Kind == ExtendedActivationKind.Launch && IsCtrlKeyDown();

        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (keyInstance.IsCurrent)
        {
            EnsureAlternateModeOverrideSignalHandle();
            keyInstance.Activated += OnActivated;
        }
        else
        {
            isRedirect = true;

            if (_forceAlternateModeOnLaunch)
            {
                SignalForceAlternateMode();
            }

            RedirectedLaunchActivationOverride.QueueIfNeeded(args);
            RedirectActivationTo(args, keyInstance);
        }

        return isRedirect;
    }

    private static void StartApplication()
    {
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool IsMailHostRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MailHostRunningMutexName, out var existingMutex))
                return false;

            existingMutex.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureMailHostRunningMutex()
    {
        try
        {
            _mailHostRunningMutex ??= new Mutex(false, MailHostRunningMutexName);
        }
        catch
        {
            _mailHostRunningMutex = null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset,
        bool bInitialState, string lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwMilliseconds, ulong nHandles,
        IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static IntPtr redirectEventHandle = IntPtr.Zero;

    public static bool TryConsumeCurrentProcessAlternateModeOverride()
    {
        if (!_forceAlternateModeOnLaunch)
            return false;

        _forceAlternateModeOnLaunch = false;
        return true;
    }

    public static bool TryConsumeRedirectedAlternateModeOverride()
    {
        try
        {
            if (_forceAlternateModeSignalHandle != null)
            {
                return _forceAlternateModeSignalHandle.WaitOne(0);
            }

            if (!EventWaitHandle.TryOpenExisting(ForceAlternateModeSignalEventName, out EventWaitHandle? signal))
                return false;

            using (signal)
            {
                return signal.WaitOne(0);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCtrlKeyDown() => (GetAsyncKeyState(VkControl) & 0x8000) != 0;

    private static void EnsureAlternateModeOverrideSignalHandle()
    {
        if (_forceAlternateModeSignalHandle != null)
            return;

        try
        {
            _forceAlternateModeSignalHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ForceAlternateModeSignalEventName);
        }
        catch
        {
            _forceAlternateModeSignalHandle = null;
        }
    }

    private static void SignalForceAlternateMode()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ForceAlternateModeSignalEventName, out EventWaitHandle? signal))
            {
                using (signal)
                {
                    signal.Set();
                }

                return;
            }

            using EventWaitHandle fallbackSignal = new(false, EventResetMode.AutoReset, ForceAlternateModeSignalEventName);
            fallbackSignal.Set();
        }
        catch
        {
            // Ignore signaling failures and continue with normal activation redirection.
        }
    }

    // Do the redirection on another thread, and use a non-blocking
    // wait method to wait for the redirection to complete.
    public static void RedirectActivationTo(AppActivationArguments args,
                                            AppInstance keyInstance)
    {
        var shouldBringWindowToForeground = ShouldBringWindowToForegroundAfterRedirection(args);

        if (shouldBringWindowToForeground)
        {
            AllowSetForegroundWindow(keyInstance.ProcessId);
        }

        redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null!);
        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle);
        });

        uint CWMO_DEFAULT = 0;
        uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(
           CWMO_DEFAULT, INFINITE, 1,
           [redirectEventHandle], out uint handleIndex);

        if (shouldBringWindowToForeground)
        {
            Process process = Process.GetProcessById((int)keyInstance.ProcessId);
            process.Refresh();

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        // When a new instance tries to launch, this event fires in the existing instance.
        // We need to notify the App to handle the activation (e.g., bring window to front, handle protocol).
        if (Application.Current is App app)
        {
            // Restore an existing managed window immediately. The complete activation route still
            // runs below to process mode changes, protocols, notifications, or window creation.
            app.TryActivateExistingWindowForRedirectedActivation(args);
            app.HandleRedirectedActivation(args);
        }
    }

    internal static bool ShouldBringWindowToForegroundAfterRedirection(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.StartupTask)
            return false;

        if (args.Kind == ExtendedActivationKind.AppNotification &&
            args.Data is AppNotificationActivatedEventArgs toastArgs)
        {
            return ToastActivationResolver.TryParse(toastArgs.Argument, out var toastArguments)
                ? ToastActivationResolver.ShouldBringToForeground(toastArguments)
                : true;
        }

        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs &&
            ForwardedNotificationActivationStore.TryRead(
                launchArgs.Arguments,
                deleteAfterRead: false,
                out var forwardedActivation))
        {
            return ToastActivationResolver.TryParse(forwardedActivation.Argument, out var forwardedToastArguments)
                ? ToastActivationResolver.ShouldBringToForeground(forwardedToastArguments)
                : true;
        }

        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs toastLaunchArgs &&
            ToastActivationResolver.TryParse(toastLaunchArgs.Arguments, out var launchToastArguments))
        {
            return ToastActivationResolver.ShouldBringToForeground(launchToastArguments);
        }

        return true;
    }
}
