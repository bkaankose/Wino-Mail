using System;
using System.IO;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Printing;

namespace MailCal.Core.Domain.Interfaces;

public interface IPrintService
{
    Task<PrintingResult> PrintAsync(nint windowHandle, string printTitle, Func<WebView2PrintSettingsModel, Task<Stream>> renderPdfStreamAsync);
}
