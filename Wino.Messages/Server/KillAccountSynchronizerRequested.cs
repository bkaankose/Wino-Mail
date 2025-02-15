using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    /// <summary>
    /// Client message that requests to kill the account synchronizer.
    /// </summary>
    /// <param name="AccountId">Account id to kill synchronizer for.</param>
    public record KillAccountSynchronizerRequested(Guid AccountId) : IClientMessage;
}
