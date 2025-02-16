using MailKit;
using MailKit.Net.Imap;
using Serilog;

namespace Wino.Core.Integration;

/// <summary>
/// Extended class for ImapClient that is used in Wino.
/// </summary>
internal class WinoImapClient : ImapClient
{
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            UnhookEvents();
        }
    }
}
