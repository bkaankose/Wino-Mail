using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Mail.WinUI.Services;

public sealed class WindowsReaderRuntimeService(NativeAppService nativeService) : IReaderRuntimeService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return nativeService.IsWebView2RuntimeAvailableAsync();
    }
}
