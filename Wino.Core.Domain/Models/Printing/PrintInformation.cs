using System;

namespace Wino.Core.Domain.Models.Printing
{
    public class PrintInformation
    {
        public PrintInformation(string pDFFilePath, string pDFTitle)
        {
            PDFFilePath = pDFFilePath ?? throw new ArgumentNullException(nameof(pDFFilePath));
            PDFTitle = pDFTitle ?? throw new ArgumentNullException(nameof(pDFTitle));
        }

        public string PDFFilePath { get; }
        public string PDFTitle { get; }
    }
}
