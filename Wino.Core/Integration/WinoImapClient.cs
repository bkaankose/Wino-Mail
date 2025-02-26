using System;
using System.Threading;
using MailKit;
using MailKit.Net.Imap;
using Serilog;

namespace Wino.Core.Integration;

/// <summary>
/// Extended class for ImapClient that is used in Wino.
/// </summary>
internal class WinoImapClient : ImapClient
{
    private int _busyCount;

    /// <summary>
    /// Gets or internally sets whether the QRESYNC extension is enabled.
    /// It is set by ImapClientPool immidiately after the authentication.
    /// </summary>
    public bool IsQResyncEnabled { get; internal set; }

    public WinoImapClient()
    {
        HookEvents();
    }

    public WinoImapClient(IProtocolLogger protocolLogger) : base(protocolLogger)
    {
        HookEvents();
    }

    private void HookEvents()
    {
        Disconnected += ClientDisconnected;
    }

    private void UnhookEvents()
    {
        Disconnected -= ClientDisconnected;
    }

    private void ClientDisconnected(object sender, DisconnectedEventArgs e)
    {
        if (e.IsRequested)
        {
            Log.Debug("Imap client is disconnected on request.");
        }
        else
        {
            Log.Debug("Imap client connection is dropped by server.");
        }
    }

    public bool IsBusy() => _busyCount > 0;

    public IDisposable GetBusyScope()
    {
        Interlocked.Increment(ref _busyCount);
        return new BusyScope(this);
    }

    private class BusyScope : IDisposable
    {
        private readonly WinoImapClient _client;
        private bool _disposed;

        public BusyScope(WinoImapClient client)
        {
            _client = client;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Interlocked.Decrement(ref _client._busyCount);
                _disposed = true;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            UnhookEvents();
        }
    }
}
