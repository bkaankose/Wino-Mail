using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using Serilog;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Integration
{
    /// <summary>
    /// Provides a pooling mechanism for ImapClient.
    /// Makes sure that we don't have too many connections to the server.
    /// Rents a connected & authenticated client from the pool all the time.
    /// TODO: Keeps the clients alive by sending NOOP command periodically.
    /// TODO: Listens to the Inbox folder for new messages.
    /// </summary>
    /// <param name="customServerInformation">Connection/Authentication info to be used to configure ImapClient.</param>
    public class ImapClientPool
    {
        // Hardcoded implementation details for ID extension if the server supports.
        // Some providers like Chinese 126 require Id to be sent before authentication.
        // We don't expose any customer data here. Therefore it's safe for now.
        // Later on maybe we can make it configurable and leave it to the user with passing
        // real implementation details.

        private readonly ImapImplementation _implementation = new ImapImplementation()
        {
            Version = "1.0",
            OS = "Windows",
            Vendor = "Wino"
        };

        private const int MaxPoolSize = 5;

        private readonly ConcurrentBag<ImapClient> _clients = [];
        private readonly SemaphoreSlim _semaphore = new(MaxPoolSize);
        private readonly CustomServerInformation _customServerInformation;
        private readonly ILogger _logger = Log.ForContext<ImapClientPool>();

        public ImapClientPool(CustomServerInformation customServerInformation)
        {
            _customServerInformation = customServerInformation;
        }

        private async Task EnsureConnectivityAsync(ImapClient client, bool isCreatedNew)
        {
            try
            {
                await EnsureConnectedAsync(client);

                if (isCreatedNew && client.IsConnected)
                {
                    // Activate supported pre-auth capabilities.
                    if (client.Capabilities.HasFlag(ImapCapabilities.Compress))
                        await client.CompressAsync();

                    // Identify if the server supports ID extension.
                    if (client.Capabilities.HasFlag(ImapCapabilities.Id))
                        await client.IdentifyAsync(_implementation);
                }

                await EnsureAuthenticatedAsync(client);

                if (isCreatedNew && client.IsAuthenticated)
                {
                    // Activate post-auth capabilities.
                    if (client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
                        await client.EnableQuickResyncAsync();
                }
            }
            catch (Exception ex)
            {
                throw new ImapClientPoolException(ex);
            }
            finally
            {
                // Release it even if it fails.
                _semaphore.Release();
            }
        }

        public async Task<ImapClient> GetClientAsync()
        {
            await _semaphore.WaitAsync();

            if (_clients.TryTake(out ImapClient item))
            {
                await EnsureConnectivityAsync(item, false);

                return item;
            }

            var client = CreateNewClient();

            await EnsureConnectivityAsync(client, true);

            return client;
        }

        public void Release(ImapClient item)
        {
            if (item != null)
            {
                _clients.Add(item);
                _semaphore.Release();
            }
        }

        public ImapClient CreateNewClient()
        {
            var client = new ImapClient();

            HttpProxyClient proxyClient = null;

            // Add proxy client if exists.
            if (!string.IsNullOrEmpty(_customServerInformation.ProxyServer))
            {
                proxyClient = new HttpProxyClient(_customServerInformation.ProxyServer, int.Parse(_customServerInformation.ProxyServerPort));
            }

            client.ProxyClient = proxyClient;

            _logger.Debug("Created new ImapClient. Current clients: {Count}", _clients.Count);

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

        public async Task EnsureConnectedAsync(ImapClient client)
        {
            if (client.IsConnected) return;

            await client.ConnectAsync(_customServerInformation.IncomingServer,
                                      int.Parse(_customServerInformation.IncomingServerPort),
                                      GetSocketOptions(_customServerInformation.IncomingServerSocketOption));
        }

        public async Task EnsureAuthenticatedAsync(ImapClient client)
        {
            if (client.IsAuthenticated) return;

            switch (_customServerInformation.IncomingAuthenticationMethod)
            {
                case ImapAuthenticationMethod.Auto:
                    break;
                case ImapAuthenticationMethod.None:
                    break;
                case ImapAuthenticationMethod.NormalPassword:
                    break;
                case ImapAuthenticationMethod.EncryptedPassword:
                    break;
                case ImapAuthenticationMethod.Ntlm:
                    break;
                case ImapAuthenticationMethod.CramMd5:
                    break;
                case ImapAuthenticationMethod.DigestMd5:
                    break;
                default:
                    break;
            }

            await client.AuthenticateAsync(_customServerInformation.IncomingServerUsername, _customServerInformation.IncomingServerPassword);
        }
    }
}
