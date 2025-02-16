using MailKit.Net.Imap;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;

namespace Wino.Core.Synchronizers.ImapSync;

internal class ImapSynchronizationStrategyProvider : IImapSynchronizationStrategyProvider
{
    private readonly QResyncSynchronizer _qResyncSynchronizer;
    private readonly CondstoreSynchronizer _condstoreSynchronizer;
    private readonly UidBasedSynchronizer _uidBasedSynchronizer;

    public ImapSynchronizationStrategyProvider(QResyncSynchronizer qResyncSynchronizer, CondstoreSynchronizer condstoreSynchronizer, UidBasedSynchronizer uidBasedSynchronizer)
    {
        _qResyncSynchronizer = qResyncSynchronizer;
        _condstoreSynchronizer = condstoreSynchronizer;
        _uidBasedSynchronizer = uidBasedSynchronizer;
    }

    public IImapSynchronizerStrategy GetSynchronizationStrategy(IImapClient client)
    {
        if (client is not WinoImapClient winoImapClient)
            throw new System.ArgumentException("Client must be of type WinoImapClient.", nameof(client));

        if (client.Capabilities.HasFlag(ImapCapabilities.QuickResync) && winoImapClient.IsQResyncEnabled) return _qResyncSynchronizer;
        if (client.Capabilities.HasFlag(ImapCapabilities.CondStore)) return _condstoreSynchronizer;

        return _uidBasedSynchronizer;
    }
}
