using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IEasConnectionService
{
    Task ValidateAsync(CustomServerInformation settings, Guid accountId, CancellationToken cancellationToken);
}
