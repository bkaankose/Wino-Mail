using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IApplicationRuntime
{
    ApplicationRuntimeState State { get; }
    event EventHandler<ApplicationRuntimeState> StateChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SynchronizeCreatedAccountAsync(MailAccount account, CancellationToken cancellationToken = default);
}
