using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

[Wino.Core.Domain.Attributes.WinoRpcService]
public interface IWinoAccountDataSyncService
{
    Task<WinoAccountSyncExportResult> ExportAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default);
    Task<WinoAccountSyncFileExportResult> ExportToJsonAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default);
    Task<WinoAccountSyncImportResult> ImportAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default);
    Task<WinoAccountSyncImportResult> ImportFromJsonAsync(string jsonContent, CancellationToken cancellationToken = default);
}
