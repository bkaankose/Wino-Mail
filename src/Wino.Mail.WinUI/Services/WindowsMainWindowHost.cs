using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Wino.Platform.Windows;
using WinRT.Interop;

namespace Wino.Mail.WinUI.Services;

internal sealed class WindowsMainWindowHost : IWindowsMainWindowHost
{
    public bool IsMainWindowAvailable => WinoApplication.MainWindow != null;

    public async Task<T> RunWithMainWindowHandleAsync<T>(Func<nint, Task<T>> operation)
    {
        var mainWindow = WinoApplication.MainWindow
            ?? throw new InvalidOperationException("Main window is not available for the Store operation.");

        var dispatcherQueue = mainWindow.DispatcherQueue
            ?? throw new InvalidOperationException("Main window dispatcher is not available for the Store operation.");

        if (dispatcherQueue.HasThreadAccess)
        {
            return await RunAsync();
        }

        return await dispatcherQueue.EnqueueAsync(RunAsync);

        Task<T> RunAsync() => operation(WindowNative.GetWindowHandle(mainWindow));
    }
}
