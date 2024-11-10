namespace Wino.Messaging.Client.Mails
{
    /// <summary>
    /// When print mail is requested.
    /// </summary>
    /// <param name="PDFFilePath">Path to PDF file that WebView2 saved the html content as PDF.</param>
    /// <param name="PrintTitle">Printer title on the dialog.</param>
    public record PrintMailRequested(string PDFFilePath, string PrintTitle);
}
