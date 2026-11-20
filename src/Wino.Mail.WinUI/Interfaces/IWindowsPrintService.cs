using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.ViewModels.Models.Printing;

namespace Wino.Mail.WinUI.Interfaces;

public interface IWindowsPrintService
{
    Task<PrintingResult> PrintAsync(nint windowHandle, string printTitle,
        Func<MailPrintOptions, Task<Stream>> renderPdfStreamAsync,
        CancellationToken cancellationToken = default);
}
