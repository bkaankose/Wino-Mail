using Android.Content;
using Android.Print;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.Rendering;
using Wino.Renderer;
using Wino.Renderer.Android;
using Wino.Renderer.Platform.Android;
using Wino.Renderer.Print;

namespace Wino.Mail.Android.Rendering;

/// <summary>
/// Prints the message a reader session shows through the system print dialog, whose "Save as PDF" printer also saves it
/// as PDF. The page that owns the reader supplies its activity and the header of the open message.
/// </summary>
public sealed class AndroidMailPrintPresenter(
    MailEngineHost engineHost,
    Func<Context?> getActivity,
    Func<MailReaderSession?> getReader,
    Func<PrintHeader> createHeader) : IMailPrintPresenter
{
    public Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (getActivity() is not { } activity || getReader() is not { Current: not null } reader
            || activity.GetSystemService(Context.PrintService) is not PrintManager printer)
        {
            return Task.FromResult(PrintingResult.Unavailable);
        }

        var header = createHeader();
        var title = string.IsNullOrWhiteSpace(request.Title) ? header.Subject ?? Translator.MailItemNoSubject : request.Title;
        var adapter = new MailPrintAdapter(activity, engineHost.Backend, reader.CreatePrintDocument, EnginePrintDocuments.CreateOptions(title, header), title);
        printer.Print(title, adapter, null);
        return Task.FromResult(PrintingResult.Submitted);
    }

    public async Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (getReader() is not { Current: not null } reader)
            return new PlatformOperationResult(PlatformOperationStatus.Unavailable, Translator.Info_PDFSaveFailedTitle);

        try
        {
            var header = createHeader();
            var document = reader.CreatePrintDocument(EnginePrintDocuments.CreateOptions(header.Subject, header));
            await Task.Run(() =>
            {
                using var stream = File.Create(path);
                AndroidPrint.WritePdf(engineHost.Backend, document, stream);
            }, cancellationToken);
            return new PlatformOperationResult(PlatformOperationStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            return new PlatformOperationResult(PlatformOperationStatus.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            Serilog.Log.Error(ex, "Saving the message as PDF failed.");
            return new PlatformOperationResult(PlatformOperationStatus.Failed, ex.Message);
        }
    }
}
