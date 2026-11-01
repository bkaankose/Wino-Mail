using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// The WebView2 sign-in dialog, presented through the dialog service on the UI thread. It shares nothing with
/// <c>WindowsOAuthBrowserBroker</c>: no default browser, no protocol activation and no external-browser waiting dialog.
/// </summary>
public sealed class WindowsEmbeddedBrowserAuthBroker : IEmbeddedBrowserAuthBroker
{
    private readonly IDispatcher _dispatcher;
    // Lazy: the dialog service reaches the authenticators that use this broker.
    private readonly Lazy<IWinoDialogService> _dialogService;

    public WindowsEmbeddedBrowserAuthBroker(IDispatcher dispatcher, Lazy<IWinoDialogService> dialogService)
    {
        _dispatcher = dispatcher;
        _dialogService = dialogService;
    }

    public async Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        cancellationToken.ThrowIfCancellationRequested();

        var presentation = Task.FromResult(EmbeddedBrowserAuthResult.Failed);
        await _dispatcher.ExecuteOnUIThread(() =>
            presentation = _dialogService.Value.ShowEmbeddedBrowserSignInDialogAsync(startUri, redirectUri, cancellationToken)).ConfigureAwait(false);

        var result = await presentation.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return result;
    }
}
