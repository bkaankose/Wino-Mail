using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication;

internal sealed class WinoGmailCodeReceiver(
    INativeAppService nativeAppService,
    IExternalBrowserAuthenticationPresenter? authenticationPresenter,
    string applicationDisplayName)
{
    private const string ProviderDisplayName = "Google";

    public async Task<GoogleAuthorizationCode> ReceiveCodeAsync(
        Func<Uri, string, Uri> authorizationUriFactory,
        CancellationToken cancellationToken)
    {
        using var listener = StartListener(out var redirectUri);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var authorizationUri = AppendPkceParameters(authorizationUriFactory(redirectUri, state), codeVerifier);

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
            if (!await nativeAppService.LaunchUriAsync(authorizationUri).ConfigureAwait(false))
            {
                if (session is null)
                    throw new InvalidOperationException("The default browser could not be opened for Google authorization.");

                // Keep waiting: the user can still copy the address into a browser by hand.
                await session.NotifyBrowserLaunchFailedAsync().ConfigureAwait(false);
            }

            var context = await listener.GetContextAsync().WaitAsync(cancellation.Token).ConfigureAwait(false);
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

    private static Uri AppendPkceParameters(Uri authorizationUri, string codeVerifier)
    {
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var separator = string.IsNullOrEmpty(authorizationUri.Query) ? "?" : "&";
        return new Uri($"{authorizationUri.AbsoluteUri}{separator}code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256");
    }

    private async Task WriteBrowserResponseAsync(HttpListenerResponse response, string? error)
    {
        var bytes = Encoding.UTF8.GetBytes(AuthorizationResultPage.Render(applicationDisplayName, error));
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.Headers["Cache-Control"] = "no-store";
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
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

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record GoogleAuthorizationCode(string Code, Uri RedirectUri, string CodeVerifier);
