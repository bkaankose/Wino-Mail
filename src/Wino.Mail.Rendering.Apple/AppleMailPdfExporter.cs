using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Platform;
using Wino.Renderer;
using Wino.Renderer.Platform.Apple;
using Wino.Renderer.Print;

namespace Wino.Mail.Rendering.Apple;

/// <summary>Save as PDF on both Apple apps: the reader's message laid out on the default paper, written as vectors.</summary>
public static class AppleMailPdfExporter
{
    public static async Task<PlatformOperationResult> ExportAsync(MailEngineHost engineHost, MailReaderSession? reader, PrintHeader header, string path,
        CancellationToken cancellationToken)
    {
        if (reader is not { Current: not null })
            return new PlatformOperationResult(PlatformOperationStatus.Unavailable, Translator.Info_PDFSaveFailedTitle);

        try
        {
            var document = reader.CreatePrintDocument(EnginePrintDocuments.CreateOptions(header.Subject, header));
            var bytes = ApplePrint.ExportPdfBytes(engineHost.Backend, document, header.Subject);
            if (bytes is null)
                return new PlatformOperationResult(PlatformOperationStatus.Failed, Translator.Info_PDFSaveFailedTitle);

            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new PlatformOperationResult(PlatformOperationStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            return new PlatformOperationResult(PlatformOperationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Error(exception, "Saving the message as PDF failed.");
            return new PlatformOperationResult(PlatformOperationStatus.Failed, exception.Message);
        }
    }
}
