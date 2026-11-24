using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.Rendering;
using Wino.Mail.Rendering.Apple;
using Wino.Renderer;
using Wino.Renderer.Print;
using Wino.Renderer.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Print and Save as PDF for the reader over the native renderer (Windows EngineMailPrintPresenter, Mac
/// MacEngineMailPrintPresenter). Print presents the system print sheet (a popover from the reader on iPad); the
/// message is laid out for the printer's paper and drawn as vectors. Save as PDF writes the pages without a sheet.
/// </summary>
public sealed class UIKitEngineMailPrintPresenter(MailEngineHost engineHost, Func<UIView?> view, Func<MailReaderSession?> reader,
    Func<PrintHeader> createHeader, IDispatcher dispatcher) : IMailPrintPresenter
{
    private static bool IsPresenting { get; set; }

    public async Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<PrintingResult> operation = Task.FromResult(PrintingResult.Unavailable);
        await dispatcher.ExecuteOnUIThread(() => operation = PresentAsync(request));
        return await operation;
    }

    private async Task<PrintingResult> PresentAsync(MailPrintRequest request)
    {
        if (reader() is not { Current: not null } session || view() is not { Window: not null } source) return PrintingResult.Unavailable;
        if (!UIPrintInteractionController.PrintingAvailable) return PrintingResult.Unavailable;
        // One print sheet at a time; a second request while it is open is abandoned, like the Mac sheet check.
        if (IsPresenting) return PrintingResult.Abandoned;

        IsPresenting = true;
        try
        {
            var header = createHeader();
            var printed = await MailPrintController.PresentAsync(engineHost.Backend,
                paper => session.CreatePrintDocument(EnginePrintDocuments.CreateOptions(request.Title, header) with { PageSize = paper }),
                request.Title, source, source.Bounds);
            return printed ? PrintingResult.Submitted : PrintingResult.Canceled;
        }
        finally
        {
            IsPresenting = false;
        }
    }

    public async Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new PlatformOperationResult(PlatformOperationStatus.Failed, "No PDF path was given.");

        MailReaderSession? session = null;
        PrintHeader? header = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            session = reader();
            header = createHeader();
        });
        if (header is null) return new PlatformOperationResult(PlatformOperationStatus.Unavailable);
        return await AppleMailPdfExporter.ExportAsync(engineHost, session, header, path, cancellationToken);
    }
}
