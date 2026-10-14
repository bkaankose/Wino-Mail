#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Turns one data-only relay push into local work, usually an inbox sync of the account in <c>cid</c>.</summary>
public interface IPushRelayNotificationHandler
{
    Task<PushRelayNotificationOutcome> HandleAsync(IReadOnlyDictionary<string, string> payload, CancellationToken cancellationToken = default);
}
