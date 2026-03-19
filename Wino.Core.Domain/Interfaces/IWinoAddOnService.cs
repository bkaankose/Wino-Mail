#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoAddOnService
{
    Task<IReadOnlyList<WinoAddOnInfo>> GetAvailableAddOnsAsync(CancellationToken cancellationToken = default);
}
