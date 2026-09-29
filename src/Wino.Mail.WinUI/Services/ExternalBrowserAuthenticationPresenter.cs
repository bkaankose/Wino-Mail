using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Dialogs;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using WinRT.Interop;
using WinUIEx;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Hosts <see cref="ExternalBrowserAuthenticationDialog"/> on the active window while an
/// authenticator waits for the browser redirect. Presentation failures never block the sign-in:
/// the browser flow keeps running and the session degrades to a no-op.
/// </summary>
public sealed partial class ExternalBrowserAuthenticationPresenter : IExternalBrowserAuthenticationPresenter
{
    private readonly IDispatcher _dispatcher;
    private readonly Lazy<INewThemeService> _themeService;
    private readonly IWinoWindowManager _windowManager;

    public ExternalBrowserAuthenticationPresenter(IDispatcher dispatcher, Lazy<INewThemeService> themeService, IWinoWindowManager windowManager)
    {
        _dispatcher = dispatcher;
        _themeService = themeService;
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
                hostWindow = WinoApplication.MainWindow;
                var xamlRoot = hostWindow?.Content?.XamlRoot;
                if (xamlRoot is null) return;

                var presentedDialog = new ExternalBrowserAuthenticationDialog
                {
                    XamlRoot = xamlRoot,
                    RequestedTheme = _themeService.Value.RootTheme.ToWindowsElementTheme(),
                    AuthorizationUri = request.AuthorizationUri,
                    Message = string.Format(Translator.ExternalBrowserAuthenticationDialog_Message, request.ProviderDisplayName)
                };

                presentedDialog.CancelRequested += (_, _) => cancelRequested();
                _ = PresentAsync(presentedDialog);

                dialog = presentedDialog;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "External browser authentication dialog could not be presented.");
        }

        return new Session(dialog, hostWindow, _dispatcher, _windowManager);
    }

    private static async Task PresentAsync(ExternalBrowserAuthenticationDialog dialog)
    {
        try
        {
            // Only one ContentDialog can be open per XamlRoot. If another one owns it, the sign-in
            // still completes in the browser; the user just misses the cancel/copy affordance.
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "External browser authentication dialog failed to show.");
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

    private sealed class Session : IExternalBrowserAuthenticationSession
    {
        private readonly ExternalBrowserAuthenticationDialog? _dialog;
        private readonly WindowEx? _hostWindow;
        private readonly IDispatcher _dispatcher;
        private readonly IWinoWindowManager _windowManager;

        public Session(ExternalBrowserAuthenticationDialog? dialog, WindowEx? hostWindow, IDispatcher dispatcher, IWinoWindowManager windowManager)
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
            var window = _hostWindow ?? WinoApplication.MainWindow;
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
