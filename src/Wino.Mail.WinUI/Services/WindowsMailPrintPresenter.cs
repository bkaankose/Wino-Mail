using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>Reader-owned delegates. The process print service remains owned by DI.</summary>
public sealed class WindowsMailPrintPresenter(
    IWindowsPrintService printService,
    Func<nint> getWindowHandle,
    Func<MailPrintOptions, Task<Stream>> renderPdfStreamAsync,
    Func<string, Task<bool>> exportPdfAsync) : IMailPrintPresenter
{
    public Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return printService.PrintAsync(getWindowHandle(), request.Title, renderPdfStreamAsync, cancellationToken);
    }

    public async Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The native export must finish before its reader/stream can be released.
        var saved = await exportPdfAsync(path);
        return cancellationToken.IsCancellationRequested
            ? new PlatformOperationResult(PlatformOperationStatus.Cancelled)
            : new PlatformOperationResult(saved ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed);
    }
}
