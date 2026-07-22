using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Authentication;

internal sealed class WinoGmailCodeReceiver(INativeAppService nativeAppService)
{
    public async Task<GoogleAuthorizationCode> ReceiveCodeAsync(
        Func<Uri, string, Uri> authorizationUriFactory,
        bool proposeCopyAuthorizationUrl,
        CancellationToken cancellationToken)
    {
        var port = ReserveLoopbackPort();
        var redirectUri = new Uri($"http://127.0.0.1:{port}/authorize/");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var authorizationUri = AppendPkceParameters(authorizationUriFactory(redirectUri, state), codeVerifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.AbsoluteUri);
        listener.Start();

        if (proposeCopyAuthorizationUrl)
        {
            WeakReferenceMessenger.Default.Send(new CopyAuthURLRequested(authorizationUri.AbsoluteUri));
        }

        if (!await nativeAppService.LaunchUriAsync(authorizationUri).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The default browser could not be opened for Google authorization.");
        }

        var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Request.QueryString;

        await WriteBrowserResponseAsync(context.Response, query["error"]).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query["error"]))
        {
            throw new InvalidOperationException($"Google authorization failed: {query["error"]}");
        }

        if (!string.Equals(query["state"], state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google authorization returned an invalid state value.");
        }

        var code = query["code"];
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Google authorization returned no authorization code.");
        }

        return new GoogleAuthorizationCode(code, redirectUri, codeVerifier);
    }

    private static Uri AppendPkceParameters(Uri authorizationUri, string codeVerifier)
    {
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var separator = string.IsNullOrEmpty(authorizationUri.Query) ? "?" : "&";
        return new Uri($"{authorizationUri.AbsoluteUri}{separator}code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256");
    }

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, string? error)
    {
        var message = string.IsNullOrWhiteSpace(error)
            ? "Authorization complete. You can return to Wino Mail."
            : "Authorization failed. You can return to Wino Mail.";
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>Wino Mail</title><p>{message}</p>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
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
