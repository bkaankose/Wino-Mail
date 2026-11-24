using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Graphics.Printing;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.Rendering;
using Wino.Renderer;
using Wino.Renderer.Platform.Windows;
using Wino.Renderer.Print;
using Wino.Renderer.WinUI;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Prints the message a reader session shows and saves it as PDF. The page that owns the reader supplies its
/// window and the header of the open message.
/// </summary>
public sealed partial class EngineMailPrintPresenter(
    MailEngineHost engineHost,
    Func<nint> getWindowHandle,
    Func<MailReaderSession?> getReader,
    Func<PrintHeader> createHeader) : IMailPrintPresenter
{
    // Windows shows one print dialog per process at a time.
    private static readonly SemaphoreSlim PrintSessionGate = new(1, 1);

    public async Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var windowHandle = getWindowHandle();
        if (windowHandle == nint.Zero || !IsWindow(windowHandle))
            return PrintingResult.Unavailable;

        if (getReader() is not { Current: not null } reader)
            return PrintingResult.Unavailable;

        if (!await PrintSessionGate.WaitAsync(0, cancellationToken))
            return PrintingResult.Unavailable;

        try
        {
            var result = await MailPrintDialog.ShowAsync(
                windowHandle,
                engineHost.Backend,
                reader.CreatePrintDocument,
                CreateOptions(request.Title),
                request.Title,
                message => Log.Debug("Mail print: {Message}", message));

            if (cancellationToken.IsCancellationRequested)
                return PrintingResult.Canceled;

            if (!result.Shown)
                return PrintingResult.Abandoned;

            return result.Completion switch
            {
                PrintTaskCompletion.Submitted => PrintingResult.Submitted,
                PrintTaskCompletion.Canceled => PrintingResult.Canceled,
                PrintTaskCompletion.Failed => PrintingResult.Failed,
                _ => PrintingResult.Abandoned
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Printing the message failed.");
            return PrintingResult.Failed;
        }
        finally
        {
            PrintSessionGate.Release();
        }
    }

    public async Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (getReader() is not { Current: not null } reader)
            return new PlatformOperationResult(PlatformOperationStatus.Unavailable, Translator.Info_PDFSaveFailedTitle);

        if (!IsPrinterInstalled(WindowsPrint.PdfPrinterName))
            return new PlatformOperationResult(PlatformOperationStatus.Failed, string.Format(Translator.Info_PDFSavePrinterMissing, WindowsPrint.PdfPrinterName));

        try
        {
            var header = createHeader();
            var document = reader.CreatePrintDocument(EnginePrintDocuments.CreateOptions(header.Subject, header));
            var result = await WindowsPrint.ExportPdfAsync(engineHost.Backend, document, path, header.Subject, cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return new PlatformOperationResult(PlatformOperationStatus.Cancelled);

            if (result.Succeeded)
                return new PlatformOperationResult(PlatformOperationStatus.Succeeded);

            Log.Warning("Saving the message as PDF ended with {Completion} (0x{HResult:X8}).", result.Completion, result.HResult);
            return new PlatformOperationResult(PlatformOperationStatus.Failed, $"{Translator.Info_PDFSaveFailedTitle} (0x{result.HResult:X8})");
        }
        catch (OperationCanceledException)
        {
            return new PlatformOperationResult(PlatformOperationStatus.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Saving the message as PDF failed.");
            return new PlatformOperationResult(PlatformOperationStatus.Failed, ex.Message);
        }
    }

    private PrintOptions CreateOptions(string? title) => EnginePrintDocuments.CreateOptions(title, createHeader());

    private static bool IsPrinterInstalled(string printerName)
    {
        if (!OpenPrinter(printerName, out var printer, nint.Zero))
            return false;

        ClosePrinter(printer);
        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint windowHandle);

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(string printerName, out nint printer, nint defaults);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint printer);
}
