using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Reader-owned print and PDF presentation over <c>WebKit.PrintOperation</c> (macOS <c>MacMailPrintPresenter</c>):
/// Print opens the GTK print dialog for the message as the reader shows it; PDF export runs the same operation without
/// a dialog on GTK's "Print to File" printer, so the PDF is paginated like a printout. Errors are results.
/// </summary>
internal sealed class LinuxMailPrintPresenter(Func<WebKit.WebView?> webView, IDispatcher dispatcher) : IMailPrintPresenter
{
    public const double PdfMarginPoints = 36;
    private static readonly TimeSpan PdfTimeout = TimeSpan.FromSeconds(60);

    private readonly HashSet<object> _pending = [];
    private bool _printing;

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
        if (string.IsNullOrWhiteSpace(path)) return new PlatformOperationResult(PlatformOperationStatus.Failed, "No PDF path was given.");
        Task<PlatformOperationResult> operation = Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable));
        await dispatcher.ExecuteOnUIThread(() => operation = BeginPdfExport(path));
        var result = await operation;
        return cancellationToken.IsCancellationRequested ? new PlatformOperationResult(PlatformOperationStatus.Cancelled) : result;
    }

    private Task<PrintingResult> BeginPrint(MailPrintRequest request)
    {
        if (webView() is not { } view || view.GetRoot() is not Gtk.Window) return Task.FromResult(PrintingResult.Unavailable);
        if (_printing) return Task.FromResult(PrintingResult.Abandoned);
        _printing = true;
        var completion = new TaskCompletionSource<PrintingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // RunDialog spins a nested main loop until the dialog closes; starting it from an idle callback keeps the caller
        // (a menu item's activation) from being held inside that loop.
        MainLoop.Post(() =>
        {
            try { RunPrintDialog(request, completion); }
            catch (Exception exception)
            {
                Serilog.Log.Warning(exception, "Printing the message failed.");
                completion.TrySetResult(PrintingResult.Failed);
                _printing = false;
            }
        });
        return completion.Task;
    }

    private void RunPrintDialog(MailPrintRequest request, TaskCompletionSource<PrintingResult> completion)
    {
        if (webView() is not { } view || view.GetRoot() is not Gtk.Window window)
        {
            completion.TrySetResult(PrintingResult.Unavailable);
            _printing = false;
            return;
        }
        var settings = Gtk.PrintSettings.New();
        settings.Set(Gtk.Constants.PRINT_SETTINGS_OUTPUT_BASENAME, Sanitize(request.Title));
        var operation = WebKit.PrintOperation.New(view);
        operation.SetPrintSettings(settings);
        var run = Start(operation, failed => failed ? PrintingResult.Failed : PrintingResult.Submitted, completion);
        var response = operation.RunDialog(window);
        if (response == WebKit.PrintOperationResponse.Cancel) run.Complete(PrintingResult.Canceled);
        else if (webView() is not { } current || current.GetRoot() is null) run.Complete(PrintingResult.Abandoned);
    }

    private Task<PlatformOperationResult> BeginPdfExport(string path)
    {
        if (webView() is not { } view || view.GetRoot() is not Gtk.Window)
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable));
        try
        {
            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            if (File.Exists(full)) File.Delete(full);

            var settings = Gtk.PrintSettings.New();
            // GTK's file backend registers its printer under the translated name.
            settings.SetPrinter(GLib.Functions.Dgettext("gtk40", "Print to File"));
            settings.Set(Gtk.Constants.PRINT_SETTINGS_OUTPUT_FILE_FORMAT, "pdf");
            settings.Set(Gtk.Constants.PRINT_SETTINGS_OUTPUT_URI, Gio.FileHelper.NewForPath(full).GetUri());
            var setup = Gtk.PageSetup.New();
            setup.SetPaperSizeAndDefaultMargins(Gtk.PaperSize.New(Gtk.Functions.PaperSizeGetDefault()));
            setup.SetTopMargin(PdfMarginPoints, Gtk.Unit.Points);
            setup.SetBottomMargin(PdfMarginPoints, Gtk.Unit.Points);
            setup.SetLeftMargin(PdfMarginPoints, Gtk.Unit.Points);
            setup.SetRightMargin(PdfMarginPoints, Gtk.Unit.Points);

            var operation = WebKit.PrintOperation.New(view);
            operation.SetPrintSettings(settings);
            operation.SetPageSetup(setup);
            var completion = new TaskCompletionSource<PlatformOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = Start(operation, failed => PdfResult(full, failed), completion);
            run.Timeout = MainLoop.Delay((uint)PdfTimeout.TotalMilliseconds,
                () => run.Complete(new PlatformOperationResult(PlatformOperationStatus.Failed, "The PDF export did not finish in time.")));
            operation.Print();
            return completion.Task;
        }
        catch (Exception exception)
        {
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, exception.Message));
        }
    }

    private static PlatformOperationResult PdfResult(string path, bool failed)
    {
        if (failed) return new PlatformOperationResult(PlatformOperationStatus.Failed, "The PDF could not be written.");
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0
            ? new PlatformOperationResult(PlatformOperationStatus.Succeeded)
            : new PlatformOperationResult(PlatformOperationStatus.Failed, "The PDF file was not written.");
    }

    private Run<T> Start<T>(WebKit.PrintOperation operation, Func<bool, T> finished, TaskCompletionSource<T> completion)
    {
        var run = new Run<T>(this, operation, completion);
        _pending.Add(run);
        // WebKit emits finished after failed too: the first signal decides.
        run.Own(GtkSignals.Connect<WebKit.PrintOperation, WebKit.PrintOperation.FailedSignalArgs>(h => operation.OnFailed += h, h => operation.OnFailed -= h, (_, args) =>
        {
            Serilog.Log.Warning("Printing failed: {Message}", args.Error.Message);
            run.Complete(finished(true));
        }));
        run.Own(GtkSignals.Connect<WebKit.PrintOperation>(h => operation.OnFinished += h, h => operation.OnFinished -= h, (_, _) => run.Complete(finished(false))));
        return run;
    }

    private static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Wino Mail";
        var invalid = Path.GetInvalidFileNameChars();
        var characters = title.Trim().Select(character => character == '/' || Array.IndexOf(invalid, character) >= 0 ? '_' : character).ToArray();
        return new string(characters);
    }

    private sealed class Run<T>(LinuxMailPrintPresenter owner, WebKit.PrintOperation operation, TaskCompletionSource<T> completion)
    {
        private readonly List<IDisposable> _subscriptions = [];

        // Holding the operation keeps its wrapper and handlers alive while the native job runs.
        public WebKit.PrintOperation Operation { get; } = operation;
        public IDisposable? Timeout { get; set; }

        public void Own(IDisposable subscription) => _subscriptions.Add(subscription);

        public void Complete(T result)
        {
            if (!completion.TrySetResult(result)) return;
            Timeout?.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            owner._pending.Remove(this);
            if (result is PrintingResult) owner._printing = false;
        }
    }
}
