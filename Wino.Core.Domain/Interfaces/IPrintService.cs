using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IPrintService
    {
        Task<PrintingResult> PrintPdfFileAsync(string pdfFilePath, string printTitle);
    }
}
