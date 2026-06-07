using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Authentication;

/// <summary>
/// Drives an interactive OAuth2 authorization-code + PKCE sign-in using the system browser and a
/// loopback redirect (RFC 8252). The system browser is preferred over an embedded webview: it is
/// more secure, reuses existing SSO/MFA sessions, and avoids the unreliable WinUI 3 web-auth broker.
/// Generic and issuer-agnostic — used by Exchange onboarding/re-auth but coupled to nothing Exchange.
/// </summary>
public interface IInteractiveOidcAuthenticator
{
    Task<OidcTokenSet> SignInAsync(OidcConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class InteractiveOidcAuthenticator : IInteractiveOidcAuthenticator
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly IOidcTokenClient _oidcTokenClient;
    private readonly INativeAppService _nativeAppService;

    public InteractiveOidcAuthenticator(IOidcTokenClient oidcTokenClient, INativeAppService nativeAppService)
    {
        _oidcTokenClient = oidcTokenClient;
        _nativeAppService = nativeAppService;
    }

    public async Task<OidcTokenSet> SignInAsync(OidcConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.RedirectUri))
            throw new OidcTokenException("A redirect URI is required for interactive sign-in.");

        var redirectUri = new Uri(configuration.RedirectUri);
        if (!redirectUri.IsLoopback)
            throw new OidcTokenException("Interactive sign-in requires a loopback redirect URI (e.g. http://localhost:8400/).");

        var discovery = await _oidcTokenClient.GetDiscoveryDocumentAsync(configuration.Authority, cancellationToken).ConfigureAwait(false);
        var pkce = _oidcTokenClient.CreatePkcePair();
        var expectedState = Guid.NewGuid().ToString("N");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SignInTimeout);

        using var listener = new LoopbackRedirectListener(redirectUri.Port);
        listener.Start();

        var authorizationUrl = _oidcTokenClient.BuildAuthorizationUrl(discovery, configuration, pkce.Challenge, expectedState);

        if (!await _nativeAppService.LaunchUriAsync(new Uri(authorizationUrl)).ConfigureAwait(false))
            throw new OidcTokenException("Failed to launch the system browser for sign-in.");

        var redirect = await listener.WaitForRedirectAsync(timeoutCts.Token).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(redirect.Error))
            throw new OidcTokenException($"Authorization failed: {redirect.Error}.");

        if (string.IsNullOrEmpty(redirect.Code))
            throw new OidcTokenException("Authorization response did not contain a code.");

        if (!string.Equals(redirect.State, expectedState, StringComparison.Ordinal))
            throw new OidcTokenException("Authorization state mismatch; possible CSRF. Sign-in aborted.");

        return await _oidcTokenClient
            .ExchangeAuthorizationCodeAsync(discovery, configuration, redirect.Code, pkce.Verifier, timeoutCts.Token)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Minimal single-shot loopback HTTP listener that captures the OAuth redirect on
/// <c>127.0.0.1:&lt;port&gt;</c>. Uses a raw TCP socket (not <c>HttpListener</c>) so it needs no URL
/// reservation or elevation. Pass port 0 to bind an ephemeral port (tests); production passes the
/// fixed port from the registered redirect URI.
/// </summary>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly TcpListener _listener;

    public LoopbackRedirectListener(int port) => _listener = new TcpListener(IPAddress.Loopback, port);

    public void Start() => _listener.Start();

    /// <summary>The actual bound port (resolved after <see cref="Start"/>; meaningful when port 0 was requested).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<RedirectResult> WaitForRedirectAsync(CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using var stream = client.GetStream();

        var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
        var query = ExtractQuery(requestLine);

        await WriteCompletionPageAsync(stream, cancellationToken).ConfigureAwait(false);

        return OAuthRedirectParser.ParseQuery(query);
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        return lineEnd >= 0 ? text[..lineEnd] : text;
    }

    private static string ExtractQuery(string requestLine)
    {
        var question = requestLine.IndexOf('?');
        if (question < 0) return string.Empty;

        var rest = requestLine[(question + 1)..];
        var space = rest.IndexOf(' ');
        return space >= 0 ? rest[..space] : rest;
    }

    private static async Task WriteCompletionPageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Sign-in complete</title></head>" +
            "<body style=\"font-family:'Segoe UI',sans-serif;text-align:center;padding-top:3em;color:#202020\">" +
            "<h2>Sign-in complete</h2><p>You can close this window and return to Wino.</p></body></html>";

        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>The query values parsed from the OAuth redirect (any may be null).</summary>
public readonly record struct RedirectResult(string Code, string State, string Error);
