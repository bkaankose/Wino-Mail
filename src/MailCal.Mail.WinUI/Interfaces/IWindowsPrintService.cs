using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Printing;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IWindowsPrintService
{
    Task<PrintingResult> PrintAsync(nint windowHandle, string printTitle,
        Func<MailPrintOptions, Task<Stream>> renderPdfStreamAsync,
        CancellationToken cancellationToken = default);
}
