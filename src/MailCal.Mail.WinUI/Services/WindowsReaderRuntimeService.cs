using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;
using MailCal.Services;

namespace MailCal.Mail.WinUI.Services;

public sealed class WindowsReaderRuntimeService(NativeAppService nativeService) : IReaderRuntimeService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return nativeService.IsWebView2RuntimeAvailableAsync();
    }
}
