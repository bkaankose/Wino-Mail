using System;
using System.IO;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Core.Domain.Interfaces;

public interface IPrintService
{
    Task<PrintingResult> PrintAsync(nint windowHandle, string printTitle, Func<WebView2PrintSettingsModel, Task<Stream>> renderPdfStreamAsync);
}
