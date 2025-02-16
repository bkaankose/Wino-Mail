using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using MimeKit.Cryptography;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Integration;

/// <summary>
/// Provides a pooling mechanism for ImapClient.
/// Makes sure that we don't have too many connections to the server.
/// Rents a connected & authenticated client from the pool all the time.
/// </summary>
/// <param name="customServerInformation">Connection/Authentication info to be used to configure ImapClient.</param>
public class ImapClientPool : IDisposable
{
    // Hardcoded implementation details for ID extension if the server supports.
    // Some providers like Chinese 126 require Id to be sent before authentication.
    // We don't expose any customer data here. Therefore it's safe for now.
    // Later on maybe we can make it configurable and leave it to the user with passing
    // real implementation details.

    private readonly ImapImplementation _implementation = new()
    {
        Version = "1.8.0",
        OS = "Windows",
        Vendor = "Wino",
        SupportUrl = "https://www.winomail.app",
        Name = "Wino Mail User",
    };

    public bool ThrowOnSSLHandshakeCallback { get; set; }
    public ImapClientPoolOptions ImapClientPoolOptions { get; }
    internal WinoImapClient IdleClient { get; set; }

    private readonly int MinimumPoolSize = 5;

    private readonly ConcurrentStack<IImapClient> _clients = [];
    private readonly SemaphoreSlim _semaphore;
    private readonly CustomServerInformation _customServerInformation;
    private readonly Stream _protocolLogStream;
    private readonly ILogger _logger = Log.ForContext<ImapClientPool>();
    private bool _disposedValue;

    public ImapClientPool(ImapClientPoolOptions imapClientPoolOptions)
    {
        _customServerInformation = imapClientPoolOptions.ServerInformation;
        _protocolLogStream = imapClientPoolOptions.ProtocolLog;

        // Set the maximum pool size to 5 or the custom value if it's greater.
        _semaphore = new(Math.Max(MinimumPoolSize, _customServerInformation.MaxConcurrentClients));

        CryptographyContext.Register(typeof(WindowsSecureMimeContext));
        ImapClientPoolOptions = imapClientPoolOptions;
    }

    /// <summary>
    /// Ensures all supported capabilities are enabled in this connection.
    /// Reconnects and reauthenticates if necessary.
    /// </summary>
    /// <param name="isCreatedNew">Whether the client has been newly created.</param>
    private async Task EnsureCapabilitiesAsync(IImapClient client, bool isCreatedNew)
    {
        try
        {
            bool isReconnected = await EnsureConnectedAsync(client);

            bool mustDoPostAuthIdentification = false;

            if ((isCreatedNew || isReconnected) && client.IsConnected)
            {
                if (client.Capabilities.HasFlag(ImapCapabilities.Compress))
                    await client.CompressAsync();

                // Identify if the server supports ID extension.
                // Some servers require it pre-authentication, some post-authentication.
                // We'll observe the response here and do it after authentication if needed.

                if (client.Capabilities.HasFlag(ImapCapabilities.Id))
                {
                    try
                    {
                        await client.IdentifyAsync(_implementation);
                    }
                    catch (ImapCommandException commandException) when (commandException.Response == ImapCommandResponse.No || commandException.Response == ImapCommandResponse.Bad)
                    {
                        mustDoPostAuthIdentification = true;
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
            }

            await EnsureAuthenticatedAsync(client);

            if ((isCreatedNew || isReconnected) && client.IsAuthenticated)
            {
                if (mustDoPostAuthIdentification) await client.IdentifyAsync(_implementation);

                // Activate post-auth capabilities.
                if (client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
                {
                    await client.EnableQuickResyncAsync().ConfigureAwait(false);

                    if (client is WinoImapClient winoImapClient) winoImapClient.IsQResyncEnabled = true;
                }
            }
        }
        catch (Exception ex)
        {
            if (ex.InnerException is ImapTestSSLCertificateException imapTestSSLCertificateException)
                throw imapTestSSLCertificateException;

            throw new ImapClientPoolException(ex, GetProtocolLogContent());
        }
        finally
        {
            // Release it even if it fails.
            _semaphore.Release();
        }
    }

    public string GetProtocolLogContent()
    {
        if (_protocolLogStream == null) return default;

        // Set the position to the beginning of the stream in case it is not already at the start
        if (_protocolLogStream.CanSeek)
            _protocolLogStream.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(_protocolLogStream, Encoding.UTF8, true, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    public async Task<IImapClient> GetClientAsync()
    {
        await _semaphore.WaitAsync();

        if (_clients.TryPop(out IImapClient item))
        {
            await EnsureCapabilitiesAsync(item, false);

            return item;
        }

        var client = CreateNewClient();

        await EnsureCapabilitiesAsync(client, true);

        return client;
    }

    public void Release(IImapClient item, bool destroyClient = false)
    {
        if (item != null)
        {
            if (destroyClient)
            {
                if (item.IsConnected)
                {
                    lock (item.SyncRoot)
                    {
                        item.Disconnect(quit: true);
                    }
                }

                _clients.TryPop(out _);
                item.Dispose();
            }
            else if (!_disposedValue)
            {
                _clients.Push(item);
            }

            _semaphore.Release();
        }
    }

    private IImapClient CreateNewClient()
    {
        WinoImapClient client = null;

        // Make sure to create a ImapClient with a protocol logger if enabled.

        client = _protocolLogStream != null
            ? new WinoImapClient(new ProtocolLogger(_protocolLogStream))
            : new WinoImapClient();

        HttpProxyClient proxyClient = null;

        // Add proxy client if exists.
        if (!string.IsNullOrEmpty(_customServerInformation.ProxyServer))
        {
            proxyClient = new HttpProxyClient(_customServerInformation.ProxyServer, int.Parse(_customServerInformation.ProxyServerPort));
        }

        client.ProxyClient = proxyClient;

        _logger.Debug("Creating new ImapClient. Current clients: {Count}", _clients.Count);

        return client;
    }

    private SecureSocketOptions GetSocketOptions(ImapConnectionSecurity connectionSecurity)
        => connectionSecurity switch
        {
            ImapConnectionSecurity.Auto => SecureSocketOptions.Auto,
            ImapConnectionSecurity.None => SecureSocketOptions.None,
            ImapConnectionSecurity.StartTls => SecureSocketOptions.StartTlsWhenAvailable,
            ImapConnectionSecurity.SslTls => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.None
        };

    /// <returns>True if the connection is newly established.</returns>
    public async Task<bool> EnsureConnectedAsync(IImapClient client)
    {
        if (client.IsConnected) return false;

        client.ServerCertificateValidationCallback = MyServerCertificateValidationCallback;

        await client.ConnectAsync(_customServerInformation.IncomingServer,
                                  int.Parse(_customServerInformation.IncomingServerPort),
                                  GetSocketOptions(_customServerInformation.IncomingServerSocketOption));

        // Print out useful information for testing.
        if (client.IsConnected && ImapClientPoolOptions.IsTestPool)
        {
            // Print supported authentication methods for the client.
            var supportedAuthMethods = client.AuthenticationMechanisms;

            if (supportedAuthMethods == null || supportedAuthMethods.Count == 0)
            {
                WriteToProtocolLog("There are no supported authentication mechanisms...");
            }
            else
            {
                WriteToProtocolLog($"Supported authentication mechanisms: {string.Join(", ", supportedAuthMethods)}");
            }
        }

        return true;

    }

    private void WriteToProtocolLog(string message)
    {
        if (_protocolLogStream == null) return;

        try
        {
            var messageBytes = Encoding.UTF8.GetBytes($"W: {message}\n");
            _protocolLogStream.Write(messageBytes, 0, messageBytes.Length);
        }
        catch (ObjectDisposedException)
        {
            Log.Warning($"Protocol log stream is disposed. Cannot write to it.");
        }
        catch (Exception)
        {

            throw;
        }
    }

    bool MyServerCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // If there are no errors, then everything went smoothly.
        if (sslPolicyErrors == SslPolicyErrors.None) return true;

        // Imap connectivity test will throw to alert the user here.
        if (ThrowOnSSLHandshakeCallback)
        {
            throw new ImapTestSSLCertificateException(certificate.Issuer, certificate.GetExpirationDateString(), certificate.GetEffectiveDateString());
        }

        return true;
    }

    public async Task EnsureAuthenticatedAsync(IImapClient client)
    {
        if (client.IsAuthenticated) return;

        var cred = new NetworkCredential(_customServerInformation.IncomingServerUsername, _customServerInformation.IncomingServerPassword);
        var prefferedAuthenticationMethod = _customServerInformation.IncomingAuthenticationMethod;

        if (prefferedAuthenticationMethod != ImapAuthenticationMethod.Auto)
        {
            // Anything beside Auto must be explicitly set for the client.
            client.AuthenticationMechanisms.Clear();

            var saslMechanism = GetSASLAuthenticationMethodName(prefferedAuthenticationMethod);

            client.AuthenticationMechanisms.Add(saslMechanism);
            var mechanism = SaslMechanism.Create(saslMechanism, cred);

            await client.AuthenticateAsync(SaslMechanism.Create(saslMechanism, cred));
        }
        else
        {
            await client.AuthenticateAsync(cred);
        }
    }

    private string GetSASLAuthenticationMethodName(ImapAuthenticationMethod method)
    {
        return method switch
        {
            ImapAuthenticationMethod.NormalPassword => "PLAIN",
            ImapAuthenticationMethod.EncryptedPassword => "LOGIN",
            ImapAuthenticationMethod.Ntlm => "NTLM",
            ImapAuthenticationMethod.CramMd5 => "CRAM-MD5",
            ImapAuthenticationMethod.DigestMd5 => "DIGEST-MD5",
            _ => "PLAIN"
        };
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _clients.ForEach(client =>
                {
                    lock (client.SyncRoot)
                    {
                        client.Disconnect(true);
                    }
                });

                _clients.ForEach(client =>
                {
                    client.Dispose();
                });

                _clients.Clear();

                _protocolLogStream?.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
