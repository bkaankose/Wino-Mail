using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Window-owned print/PDF presentation, attached only while the reader is active.</summary>
public interface IMailPrintPresenter
{
    Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default);
    Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default);
}
