using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;

namespace Wino.Authentication;

/// <summary>
/// Desktop receiver: listens on a loopback HTTP address, opens the default browser and serves the
/// result page when Google redirects back. Windows and macOS register it.
/// </summary>
public sealed class WinoGmailCodeReceiver(
    IExternalLauncher externalLauncher,
    IExternalBrowserAuthenticationPresenter? authenticationPresenter,
    string applicationDisplayName) : IGoogleAuthorizationCodeReceiver
{
    private const string ProviderDisplayName = "Google";
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    public async Task<GoogleAuthorizationCode> ReceiveCodeAsync(
        Func<Uri, string, Uri> authorizationUriFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var listener = StartListener(out var redirectUri);
        var state = GoogleAuthorizationPkce.CreateState();
        var codeVerifier = GoogleAuthorizationPkce.CreateCodeVerifier();
        var authorizationUri = GoogleAuthorizationPkce.AppendChallenge(authorizationUriFactory(redirectUri, state), codeVerifier);

        // The in-app session lets the user cancel the wait or copy the address when the
        // browser never shows up. It is closed as soon as the redirect arrives or the flow fails.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = authenticationPresenter is null
            ? null
            : await authenticationPresenter.ShowAsync(
                new ExternalBrowserAuthenticationRequest(ProviderDisplayName, authorizationUri),
                () => RequestCancellation(cancellation)).ConfigureAwait(false);

        try
        {
            var launch = await externalLauncher.LaunchUriAsync(authorizationUri, cancellation.Token).ConfigureAwait(false);
            if (launch.Status == PlatformOperationStatus.Cancelled)
                throw new OperationCanceledException(cancellation.Token);

            if (!launch.IsSuccess)
            {
                if (session is null)
                {
                    launch.ThrowIfNotSucceeded();
                    throw new InvalidOperationException("The default browser could not be opened for Google authorization.");
                }

                // Keep waiting: the user can still copy the address into a browser by hand.
                await session.NotifyBrowserLaunchFailedAsync().ConfigureAwait(false);
            }

            var contextTask = listener.GetContextAsync();
            HttpListenerContext context;

            try
            {
                context = await contextTask.WaitAsync(CallbackTimeout, cancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                // Disposing the listener aborts the pending wait; observe it so the abort never
                // surfaces later as an unobserved task exception.
                ObserveAbandonedCallback(contextTask);
                throw;
            }

            var query = context.Request.QueryString;

            // The browser owns the foreground now; ask the app to come back before the result is read.
            if (session is not null)
                await session.NotifyRedirectReceivedAsync().ConfigureAwait(false);

            if (!string.Equals(query["state"], state, StringComparison.Ordinal))
            {
                await WriteBrowserResponseAsync(context.Response, "invalid_state").ConfigureAwait(false);
                throw new InvalidOperationException("Google authorization returned an invalid state value.");
            }

            if (!string.IsNullOrWhiteSpace(query["error"]))
            {
                await WriteBrowserResponseAsync(context.Response, query["error"]).ConfigureAwait(false);
                throw new InvalidOperationException($"Google authorization failed: {query["error"]}");
            }

            var code = query["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteBrowserResponseAsync(context.Response, "invalid_code").ConfigureAwait(false);
                throw new InvalidOperationException("Google authorization returned no authorization code.");
            }

            await WriteBrowserResponseAsync(context.Response, null).ConfigureAwait(false);
            return new GoogleAuthorizationCode(code, redirectUri, codeVerifier);
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void RequestCancellation(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The redirect already completed the flow; nothing left to cancel.
        }
    }

    private static void ObserveAbandonedCallback(Task<HttpListenerContext> contextTask)
        => contextTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private Task WriteBrowserResponseAsync(HttpListenerResponse response, string? error)
    {
        var bytes = Encoding.UTF8.GetBytes(AuthorizationResultPage.Render(applicationDisplayName, error));
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.Headers["Cache-Control"] = "no-store";
        // Wait for the final response send before disposing the listener. An asynchronous
        // stream write followed by immediate listener teardown can reset the callback socket.
        response.KeepAlive = false;
        response.Close(bytes, willBlock: true);
        return Task.CompletedTask;
    }

    private static HttpListener StartListener(out Uri redirectUri)
    {
        for (var attempt = 0; ; attempt++)
        {
            redirectUri = new Uri($"http://127.0.0.1:{ReserveLoopbackPort()}/authorize/");
            var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri.AbsoluteUri);
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
