using System;
using MailKit;
using MailKit.Net.Imap;
using Serilog;
using Wino.Core.Diagnostics;

namespace Wino.Core.Integration;

/// <summary>
/// Extended class for ImapClient that is used in Wino.
/// </summary>
public class WinoImapClient : ImapClient
{
    internal DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

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
        if (protocolLogger is WinoProtocolLogger logger) ConnectionId = logger.ConnectionId;
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
            Log.Debug("IMAP connection {ConnectionId} disconnected on request.", ConnectionId);
        }
        else
        {
            Log.Debug("IMAP connection {ConnectionId} disconnected unexpectedly.", ConnectionId);
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
