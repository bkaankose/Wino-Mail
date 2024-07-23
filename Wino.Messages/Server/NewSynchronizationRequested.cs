﻿using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Messaging.Server
{
    /// <summary>
    /// Triggers a new synchronization if possible.
    /// </summary>
    /// <param name="Options">Options for synchronization.</param>
    public record NewSynchronizationRequested(SynchronizationOptions Options) : IClientMessage;
}
