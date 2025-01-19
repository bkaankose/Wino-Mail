using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;

namespace Wino.Core.Synchronizers.ImapSync
{
    /// <summary>
    /// Uid based IMAP Synchronization strategy.
    /// </summary>
    internal class UidBasedSynchronizer : IImapSynchronizerStrategy
    {
        public Task<List<string>> HandleSynchronizationAsync(IImapClient client, MailItemFolder folder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default)
        {
            if (client is not WinoImapClient winoClient)
                throw new ArgumentException("Client must be of type WinoImapClient.", nameof(client));

            return Task.FromResult(new List<string>());
        }
    }
}
