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
/// printers, preview and "Save as PDF". PDF export runs the same operation without panels and saves
/// the job to the chosen path, so it is paginated like the print dialog's PDF. Like Windows, both use the message exactly as the reader renders it (current theme).
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
            var info = CreatePrintInfo();
            var completion = new TaskCompletionSource<PrintingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            RunPrintOperation(view, window, info, request.Title, showPanels: true,
                success => completion.TrySetResult(success ? PrintingResult.Submitted : PrintingResult.Canceled));
            return completion.Task;
        }
        catch (Exception)
        {
            return Task.FromResult(PrintingResult.Failed);
        }
    }

    /// <summary>
    /// Runs the same print operation as <see cref="PrintAsync"/> without panels, with the job saved
    /// to <paramref name="path"/>, so the PDF is paginated exactly like the print dialog's "Save as PDF".
    /// </summary>
    private Task<PlatformOperationResult> BeginPdfExport(string path)
    {
        var view = webView();
        if (view?.Window is not { } window) return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable));
        if (window.AttachedSheet is not null)
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, "Another sheet is open on the reader window."));

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var info = CreatePrintInfo();
            // Margins like a browser's PDF export; the shared defaults (1in+) waste a lot of the page.
            info.LeftMargin = info.RightMargin = PdfMargin;
            info.TopMargin = info.BottomMargin = PdfMargin;
            info.JobDisposition = SaveJobDisposition;
            // The path comes from a save or folder panel (sandbox scope); keep it exactly as given.
            using var url = NSUrl.FromFilename(path);
            info.Dictionary[JobSavingUrlKey] = url;

            var completion = new TaskCompletionSource<PlatformOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            RunPrintOperation(view, window, info, Path.GetFileNameWithoutExtension(path), showPanels: false, success =>
            {
                if (!success)
                    completion.TrySetResult(new PlatformOperationResult(PlatformOperationStatus.Failed, "The PDF could not be created."));
                else if (!File.Exists(path))
                    completion.TrySetResult(new PlatformOperationResult(PlatformOperationStatus.Failed, "The PDF was not written."));
                else
                    completion.TrySetResult(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
            });
            return completion.Task;
        }
        catch (Exception exception)
        {
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, exception.Message));
        }
    }

    private const float PdfMargin = 36;

    // AppKit's NSPrintSaveJob and NSPrintJobSavingURL; the binding has no named constants for them.
    private static NSString SaveJobDisposition => AppKitString(ref _saveJobDisposition, "NSPrintSaveJob");
    private static NSString JobSavingUrlKey => AppKitString(ref _jobSavingUrlKey, "NSPrintJobSavingURL");
    private static NSString? _saveJobDisposition;
    private static NSString? _jobSavingUrlKey;

    private static NSString AppKitString(ref NSString? cache, string symbol)
    {
        if (cache is not null) return cache;
        var appKit = Dlfcn.dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 0);
        return cache = Dlfcn.GetStringConstant(appKit, symbol) ?? throw new InvalidOperationException($"AppKit does not export {symbol}.");
    }

    private static NSPrintInfo CreatePrintInfo()
    {
        var info = (NSPrintInfo)NSPrintInfo.SharedPrintInfo.Copy();
        // Fit the page width like a browser print; long messages continue onto further pages.
        info.HorizontalPagination = NSPrintingPaginationMode.Fit;
        info.VerticalPagination = NSPrintingPaginationMode.Auto;
        info.HorizontallyCentered = false;
        info.VerticallyCentered = false;
        return info;
    }

    private void RunPrintOperation(WKWebView view, NSWindow window, NSPrintInfo info, string? title, bool showPanels, Action<bool> completed)
    {
        var operation = view.GetPrintOperation(info);
        operation.JobTitle = string.IsNullOrWhiteSpace(title) ? "Wino Mail" : title;
        operation.ShowsPrintPanel = showPanels;
        operation.ShowsProgressPanel = showPanels;
        // WebKit's print view starts with an empty frame; without one the operation prints nothing.
        if (operation.View is { } printView) printView.Frame = view.Bounds;

        var completion = new PrintCompletion(this, completed);
        _pending.Add(completion);
        operation.RunOperationModal(window, completion, new Selector(PrintCompletion.SelectorName), IntPtr.Zero);
    }

    /// <summary>Receives printOperationDidRun:success:contextInfo: when the operation finishes.</summary>
    private sealed class PrintCompletion(MacMailPrintPresenter owner, Action<bool> completed) : NSObject
    {
        public const string SelectorName = "printOperationDidRun:success:contextInfo:";

        [Export(SelectorName)]
        public void DidRun(NSPrintOperation operation, bool success, IntPtr contextInfo)
        {
            owner._pending.Remove(this);
            completed(success);
        }
    }
}
