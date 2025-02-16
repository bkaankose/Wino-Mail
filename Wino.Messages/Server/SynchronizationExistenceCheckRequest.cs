using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Synchronization
{
    /// <summary>
    /// Client request to server to check whether given account id is in the middle of synchronization.
    /// </summary>
    /// <param name="AccountId">Account id to check sync existence for.</param>
    public record SynchronizationExistenceCheckRequest(Guid AccountId) : IClientMessage;
}
