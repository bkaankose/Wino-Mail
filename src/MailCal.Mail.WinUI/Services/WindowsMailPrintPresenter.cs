using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Platform;
using MailCal.Core.Domain.Models.Printing;
using MailCal.Mail.WinUI.Interfaces;

namespace MailCal.Mail.WinUI.Services;

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
