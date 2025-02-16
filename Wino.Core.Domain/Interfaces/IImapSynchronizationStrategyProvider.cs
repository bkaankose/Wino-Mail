using MailKit.Net.Imap;

namespace Wino.Core.Domain.Interfaces
{
    /// <summary>
    /// Provides a synchronization strategy for synchronizing IMAP folders based on the server capabilities.
    /// </summary>
    public interface IImapSynchronizationStrategyProvider
    {
        IImapSynchronizerStrategy GetSynchronizationStrategy(IImapClient client);
    }
}
