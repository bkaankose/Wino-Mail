using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.Rendering;
using Wino.Mail.Rendering.Apple;
using Wino.Renderer;
using Wino.Renderer.AppKit;
using Wino.Renderer.Print;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Print and Save as PDF for the reader over the native renderer (Windows EngineMailPrintPresenter). Print shows the
/// Mac print panel as a sheet on the reader's window; the message is laid out for the panel's paper and drawn as
/// vectors, so the panel's own PDF menu saves the same pages. Save as PDF writes the pages without a panel.
/// </summary>
public sealed class MacEngineMailPrintPresenter(MailEngineHost engineHost, Func<NSView?> view, Func<MailReaderSession?> reader,
    Func<PrintHeader> createHeader, IDispatcher dispatcher) : IMailPrintPresenter
{
    public async Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<PrintingResult> operation = Task.FromResult(PrintingResult.Unavailable);
        await dispatcher.ExecuteOnUIThread(() => operation = ShowPanelAsync(request));
        return await operation;
    }

    private async Task<PrintingResult> ShowPanelAsync(MailPrintRequest request)
    {
        if (reader() is not { Current: not null } session || view()?.Window is not { } window) return PrintingResult.Unavailable;
        if (window.AttachedSheet is not null) return PrintingResult.Abandoned;

        var header = createHeader();
        var printed = await MailPrintPanel.ShowAsync(window, engineHost.Backend,
            paper => session.CreatePrintDocument(EnginePrintDocuments.CreateOptions(request.Title, header) with { PageSize = paper }), request.Title);
        return printed ? PrintingResult.Submitted : PrintingResult.Canceled;
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
        return await AppleMailPdfExporter.ExportAsync(engineHost, session, header!, path, cancellationToken);
    }
}
