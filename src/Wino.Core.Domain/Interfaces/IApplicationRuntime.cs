using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IApplicationRuntime
{
    ApplicationRuntimeState State { get; }
    event EventHandler<ApplicationRuntimeState> StateChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SynchronizeCreatedAccountAsync(MailAccount account, CancellationToken cancellationToken = default);
}
