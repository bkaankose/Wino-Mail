using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoPurchaseReconciliationService
{
    Task<WinoPurchaseRefreshResult> RefreshAsync(bool checkoutCompleted = false, CancellationToken cancellationToken = default);
}
