#nullable enable
using System;
using System.Threading.Tasks;
using Serilog;
using Windows.Security.Credentials.UI;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>Windows Hello through the window-handle interop a desktop app needs, and the PIN dialog.</summary>
public sealed class WindowsAppLockPlatform : IAppLockPlatform
{
    private readonly Lazy<IWinoDialogService> _dialogService;
    private readonly WinUIDispatcher _dispatcher;
    private readonly Func<IntPtr> _getWindowHandle;

    public WindowsAppLockPlatform(Lazy<IWinoDialogService> dialogService, WinUIDispatcher dispatcher, Func<IntPtr> getWindowHandle)
    {
        _dialogService = dialogService;
        _dispatcher = dispatcher;
        _getWindowHandle = getWindowHandle;
    }

    public bool IsAvailable => true;

    public async Task<bool> IsBiometricAvailableAsync()
    {
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Windows Hello availability could not be checked.");
            return false;
        }
    }

    public async Task<bool> VerifyBiometricAsync()
    {
        try
        {
            var windowHandle = _getWindowHandle();
            if (windowHandle == IntPtr.Zero)
            {
                Log.Warning("Windows Hello was not shown because no window is available to own the prompt.");
                return false;
            }

            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(windowHandle, Translator.AppLock_HelloPrompt);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Windows Hello verification failed.");
            return false;
        }
    }

    public async Task<bool> PromptVerificationAsync()
    {
        if (_dispatcher.HasThreadAccess)
            return await _dialogService.Value.ShowAppLockVerificationDialogAsync();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _dispatcher.ExecuteOnUIThread(() => _ = PromptOnUiThreadAsync(completion));

        return await completion.Task;
    }

    private async Task PromptOnUiThreadAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            completion.TrySetResult(await _dialogService.Value.ShowAppLockVerificationDialogAsync());
        }
        catch (Exception exception)
        {
            Log.Error(exception, "The app lock verification dialog failed.");
            completion.TrySetResult(false);
        }
    }
}
