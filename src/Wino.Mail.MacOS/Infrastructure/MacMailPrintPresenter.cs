using AppKit;
using Foundation;
using ObjCRuntime;
using WebKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Reader-owned print and PDF presentation (Windows WindowsMailPrintPresenter). Printing runs the
/// web view's NSPrintOperation as a sheet on the reader's window, so the system print panel offers
/// printers, preview and "Save as PDF". PDF export writes WKWebView.CreatePdf output to the chosen
/// path. Like Windows, both use the message exactly as the reader renders it (current theme).
/// </summary>
public sealed class MacMailPrintPresenter(Func<WKWebView?> webView, IDispatcher dispatcher) : IMailPrintPresenter
{
    // The print operation does not retain its completion delegate; keep it alive until it reports.
    private readonly HashSet<PrintCompletion> _pending = [];

    public async Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<PrintingResult> operation = Task.FromResult(PrintingResult.Unavailable);
        await dispatcher.ExecuteOnUIThread(() => operation = BeginPrint(request));
        return await operation;
    }

    public async Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return new PlatformOperationResult(PlatformOperationStatus.Failed, "No PDF path was given.");

        Task<PlatformOperationResult> operation = Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable));
        await dispatcher.ExecuteOnUIThread(() => operation = BeginPdfExport(path));
        var result = await operation;
        return cancellationToken.IsCancellationRequested
            ? new PlatformOperationResult(PlatformOperationStatus.Cancelled)
            : result;
    }

    private Task<PrintingResult> BeginPrint(MailPrintRequest request)
    {
        var view = webView();
        if (view?.Window is not { } window) return Task.FromResult(PrintingResult.Unavailable);
        if (window.AttachedSheet is not null) return Task.FromResult(PrintingResult.Abandoned);

        try
        {
            var info = (NSPrintInfo)NSPrintInfo.SharedPrintInfo.Copy();
            // Fit the page width like a browser print; long messages continue onto further pages.
            info.HorizontalPagination = NSPrintingPaginationMode.Fit;
            info.VerticalPagination = NSPrintingPaginationMode.Auto;
            info.HorizontallyCentered = false;
            info.VerticallyCentered = false;

            var operation = view.GetPrintOperation(info);
            operation.JobTitle = string.IsNullOrWhiteSpace(request.Title) ? "Wino Mail" : request.Title;
            operation.ShowsPrintPanel = true;
            operation.ShowsProgressPanel = true;
            // WebKit's print view starts with an empty frame; without one the operation prints nothing.
            if (operation.View is { } printView) printView.Frame = view.Bounds;

            var completion = new PrintCompletion(this);
            _pending.Add(completion);
            operation.RunOperationModal(window, completion, new Selector(PrintCompletion.SelectorName), IntPtr.Zero);
            return completion.Completion;
        }
        catch (Exception)
        {
            return Task.FromResult(PrintingResult.Failed);
        }
    }

    private Task<PlatformOperationResult> BeginPdfExport(string path)
    {
        var view = webView();
        if (view is null) return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable));

        var completion = new TaskCompletionSource<PlatformOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A null rect captures the whole document, not only the visible part of the reader.
        var configuration = new WKPdfConfiguration();
        view.CreatePdf(configuration, (data, error) =>
        {
            try
            {
                if (error is not null || data is null)
                {
                    completion.TrySetResult(new PlatformOperationResult(PlatformOperationStatus.Failed, error?.LocalizedDescription));
                    return;
                }
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var saved = data.Save(path, true, out var saveError);
                completion.TrySetResult(saved
                    ? new PlatformOperationResult(PlatformOperationStatus.Succeeded)
                    : new PlatformOperationResult(PlatformOperationStatus.Failed, saveError?.LocalizedDescription));
            }
            catch (Exception exception)
            {
                completion.TrySetResult(new PlatformOperationResult(PlatformOperationStatus.Failed, exception.Message));
            }
            finally
            {
                configuration.Dispose();
            }
        });
        return completion.Task;
    }

    /// <summary>Receives printOperationDidRun:success:contextInfo: from the sheet.</summary>
    private sealed class PrintCompletion(MacMailPrintPresenter owner) : NSObject
    {
        public const string SelectorName = "printOperationDidRun:success:contextInfo:";

        private readonly TaskCompletionSource<PrintingResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PrintingResult> Completion => _completion.Task;

        [Export(SelectorName)]
        public void DidRun(NSPrintOperation operation, bool success, IntPtr contextInfo)
        {
            owner._pending.Remove(this);
            _completion.TrySetResult(success ? PrintingResult.Submitted : PrintingResult.Canceled);
        }
    }
}
