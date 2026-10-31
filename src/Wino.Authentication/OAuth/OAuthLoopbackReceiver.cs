#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication.OAuth;

/// <summary>
/// RFC 8252 loopback redirect on 127.0.0.1. A raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/>,
/// which URL ACLs or policy can block on Windows.
/// </summary>
internal sealed class OAuthLoopbackReceiver(IExternalLauncher launcher, IExternalBrowserAuthenticationPresenter? presenter)
{
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly ILogger Logger = Log.ForContext<OAuthLoopbackReceiver>();

    public sealed record Result(string Code, Uri RedirectUri);

    public async Task<Result> ReceiveAsync(
        Func<Uri, Uri> authorizationUriFactory,
        string expectedState,
        string issuer,
        string providerDisplayName,
        int? fixedPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var listener = new TcpListener(IPAddress.Loopback, fixedPort ?? 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "The sign-in could not listen for its redirect on this device.", ex);
        }

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = new Uri($"http://127.0.0.1:{port}/");
            var authorizationUri = authorizationUriFactory(redirectUri);

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var userCancel = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, userCancel.Token);

            var session = presenter is null
                ? null
                : await presenter.ShowAsync(
                    new ExternalBrowserAuthenticationRequest(providerDisplayName, authorizationUri),
                    () => TryCancel(userCancel)).ConfigureAwait(false);

            try
            {
                var launch = await launcher.LaunchUriAsync(authorizationUri, linked.Token).ConfigureAwait(false);
                if (launch.Status == PlatformOperationStatus.Cancelled)
                    throw new OperationCanceledException(linked.Token);

                if (!launch.IsSuccess)
                {
                    if (session is null)
                        throw new OAuthSignInException(OAuthSignInFailureKind.Failed, launch.ErrorMessage ?? "The browser could not be opened for sign-in.");

                    // The waiting dialog offers the address to copy into a browser by hand.
                    await session.NotifyBrowserLaunchFailedAsync().ConfigureAwait(false);
                }

                var code = await AcceptCallbackAsync(listener, expectedState, issuer, session, linked.Token).ConfigureAwait(false);
                return new Result(code, redirectUri);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (timeoutSource.IsCancellationRequested)
                    throw new OAuthSignInException(OAuthSignInFailureKind.TimedOut, "The sign-in timed out.", ex);

                throw new OAuthSignInException(OAuthSignInFailureKind.Cancelled, "The sign-in was cancelled.", ex);
            }
            finally
            {
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> AcceptCallbackAsync(
        TcpListener listener,
        string expectedState,
        string issuer,
        IExternalBrowserAuthenticationSession? session,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            string? target;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readTimeout.CancelAfter(ConnectionReadTimeout);
                try
                {
                    target = await ReadRequestTargetAsync(stream, readTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    continue;
                }
            }

            if (target is null || !TrySplitTarget(target, out var path, out var query) || path != "/" || !LooksLikeCallback(query))
            {
                await TryWriteResponseAsync(stream, "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (session is not null)
                await session.NotifyRedirectReceivedAsync().ConfigureAwait(false);

            try
            {
                var code = OAuthAuthorizationCallback.ReadCode(query, hasFragment: false, expectedState, issuer);
                await TryWriteResponseAsync(stream, "200 OK", RenderPage(succeeded: true), cancellationToken).ConfigureAwait(false);
                return code;
            }
            catch (OAuthSignInException)
            {
                await TryWriteResponseAsync(stream, "200 OK", RenderPage(succeeded: false), cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Reads the request line, then drains the headers so closing the socket does not reset the browser's connection.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        string? requestLine = null;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);

            if (requestLine is null)
            {
                var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (lineEnd >= 0)
                    requestLine = text[..lineEnd];
            }

            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        if (requestLine is null)
            return null;

        var parts = requestLine.Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) ? parts[1] : null;
    }

    private static bool TrySplitTarget(string target, out string path, out string query)
    {
        path = string.Empty;
        query = string.Empty;
        if (!target.StartsWith('/'))
            return false;

        var separator = target.IndexOf('?');
        path = separator < 0 ? target : target[..separator];
        query = separator < 0 ? string.Empty : target[(separator + 1)..];
        return true;
    }

    private static bool LooksLikeCallback(string query)
        => query.Contains("state=", StringComparison.Ordinal)
           || query.Contains("code=", StringComparison.Ordinal)
           || query.Contains("error=", StringComparison.Ordinal);

    private static async Task TryWriteResponseAsync(NetworkStream stream, string status, string body, CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {content.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");

        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            Logger.Debug("The browser closed the sign-in redirect connection before the page was sent.");
        }
    }

    private static string RenderPage(bool succeeded)
    {
        var heading = succeeded ? "You're signed in" : "Sign-in did not complete";
        var message = succeeded
            ? "You can close this tab and return to Wino Mail."
            : "Close this tab and return to Wino Mail to try again.";

        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
               $"<title>Wino Mail</title></head><body style=\"font-family:system-ui,sans-serif;margin:3rem auto;max-width:32rem;padding:0 1rem\">" +
               $"<h1 style=\"font-size:1.4rem\">{heading}</h1><p>{message}</p></body></html>";
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
