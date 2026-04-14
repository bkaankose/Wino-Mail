using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppLifecycle;
using Wino.Mail.WinUI.Activation;

namespace Wino.Mail.WinUI;

public class Program
{
    private const string SingleInstanceKey = "WinoMailSingleInstance";
    private const string ForceAlternateModeSignalEventName = "Local\\WinoMailForceAlternateMode";
    private const int VkControl = 0x11;

    private static bool _forceAlternateModeOnLaunch;
    private static EventWaitHandle? _forceAlternateModeSignalHandle;

    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        bool isRedirect = DecideRedirection();

        if (!isRedirect)
        {
            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                var app = new App();
                _ = app.HandleInitialActivationAsync();
            });
        }

        return 0;
    }

    private static bool DecideRedirection()
    {
        bool isRedirect = false;
        AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
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

            RedirectActivationTo(args, keyInstance);
        }

        return isRedirect;
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

        if (ShouldBringWindowToForegroundAfterRedirection(args))
        {
            Process process = Process.GetProcessById((int)keyInstance.ProcessId);
            SetForegroundWindow(process.MainWindowHandle);
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        // When a new instance tries to launch, this event fires in the existing instance.
        // We need to notify the App to handle the activation (e.g., bring window to front, handle protocol).
        if (Application.Current is App app)
        {
            app.HandleRedirectedActivation(args);
        }
    }

    private static bool ShouldBringWindowToForegroundAfterRedirection(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.AppNotification &&
            args.Data is AppNotificationActivatedEventArgs toastArgs)
        {
            return ToastActivationResolver.TryParse(toastArgs.Argument, out var toastArguments)
                ? ToastActivationResolver.ShouldBringToForeground(toastArguments)
                : true;
        }

        if (args.Data is Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs classicToastArgs)
        {
            return ToastActivationResolver.TryParse(classicToastArgs.Argument, out var toastArguments)
                ? ToastActivationResolver.ShouldBringToForeground(toastArguments)
                : true;
        }

        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs &&
            ToastActivationResolver.TryParse(launchArgs.Arguments, out var launchToastArguments))
        {
            return ToastActivationResolver.ShouldBringToForeground(launchToastArguments);
        }

        return true;
    }
}
