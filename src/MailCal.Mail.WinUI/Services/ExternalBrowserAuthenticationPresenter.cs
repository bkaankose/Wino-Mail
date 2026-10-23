using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain.Interfaces;
using MailCal.Dialogs;
using MailCal.Mail.WinUI.Interfaces;
using WinRT.Interop;
using WinUIEx;

namespace MailCal.Mail.WinUI.Services;

/// <summary>
/// Keeps <see cref="ExternalBrowserAuthenticationDialog"/> open through the dialog service while an
/// authenticator waits for the browser redirect. Presentation failures never block the sign-in:
/// the browser flow keeps running and the session degrades to a no-op.
/// </summary>
public sealed partial class ExternalBrowserAuthenticationPresenter : IExternalBrowserAuthenticationPresenter
{
    private readonly IDispatcher _dispatcher;
    // Lazy: the dialog service reaches the authenticators that use this presenter.
    private readonly Lazy<IMailCalDialogService> _dialogService;
    private readonly IMailCalWindowManager _windowManager;

    public ExternalBrowserAuthenticationPresenter(IDispatcher dispatcher, Lazy<IMailCalDialogService> dialogService, IMailCalWindowManager windowManager)
    {
        _dispatcher = dispatcher;
        _dialogService = dialogService;
        _windowManager = windowManager;
    }

    public async Task<IExternalBrowserAuthenticationSession> ShowAsync(ExternalBrowserAuthenticationRequest request, Action cancelRequested)
    {
        ExternalBrowserAuthenticationDialog? dialog = null;
        WindowEx? hostWindow = null;

        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                hostWindow = MailCalApplication.MainWindow;
                dialog = _dialogService.Value.OpenExternalBrowserAuthenticationDialog(request, cancelRequested);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "External browser authentication dialog could not be presented.");
        }

        return new Session(dialog, hostWindow, _dispatcher, _windowManager);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

    private sealed class Session : IExternalBrowserAuthenticationSession
    {
        private readonly ExternalBrowserAuthenticationDialog? _dialog;
        private readonly WindowEx? _hostWindow;
        private readonly IDispatcher _dispatcher;
        private readonly IMailCalWindowManager _windowManager;

        public Session(ExternalBrowserAuthenticationDialog? dialog, WindowEx? hostWindow, IDispatcher dispatcher, IMailCalWindowManager windowManager)
        {
            _dialog = dialog;
            _hostWindow = hostWindow;
            _dispatcher = dispatcher;
            _windowManager = windowManager;
        }

        public Task NotifyBrowserLaunchFailedAsync()
            => RunOnDialogAsync(dialog => dialog.IsBrowserLaunchFailed = true);

        public Task NotifyRedirectReceivedAsync()
            => RunOnUIThreadAsync(BringHostWindowToForeground, "foreground request");

        public async ValueTask DisposeAsync()
            => await RunOnDialogAsync(dialog => dialog.Complete()).ConfigureAwait(false);

        private void BringHostWindowToForeground()
        {
            var window = _hostWindow ?? MailCalApplication.MainWindow;
            if (window is null) return;

            // Restores a minimized or hidden window and activates it. Windows may still refuse to
            // steal focus from the browser; it then flashes the taskbar button instead, which is
            // the intended fallback rather than a failure.
            _windowManager.ActivateWindow(window);
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
        }

        private Task RunOnDialogAsync(Action<ExternalBrowserAuthenticationDialog> action)
            => _dialog is null
                ? Task.CompletedTask
                : RunOnUIThreadAsync(() => action(_dialog), "dialog update");

        private async Task RunOnUIThreadAsync(Action action, string operation)
        {
            try
            {
                await _dispatcher.ExecuteOnUIThread(action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "External browser authentication {Operation} failed.", operation);
            }
        }
    }
}
