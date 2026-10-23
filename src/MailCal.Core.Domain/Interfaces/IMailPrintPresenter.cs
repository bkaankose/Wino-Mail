using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Platform;
using MailCal.Core.Domain.Models.Printing;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>Window-owned print/PDF presentation, attached only while the reader is active.</summary>
public interface IMailPrintPresenter
{
    Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default);
    Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default);
}
