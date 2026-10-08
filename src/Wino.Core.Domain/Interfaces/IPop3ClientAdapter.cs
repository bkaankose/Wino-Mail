using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IPop3ClientAdapter : IDisposable
{
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    bool SupportsUids { get; }
    int Count { get; }

    Task ConnectAndAuthenticateAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken cancellationToken = default);
    Task<HeaderList> GetMessageHeadersAsync(int index, CancellationToken cancellationToken = default);
    Task<MimeMessage> GetMessageAsync(int index, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(int index, CancellationToken cancellationToken = default);
    Task DisconnectAsync(bool commitDeletions, CancellationToken cancellationToken = default);
}

public interface IPop3ClientFactory
{
    IPop3ClientAdapter Create(Guid accountId, bool enableProtocolLog);
}
