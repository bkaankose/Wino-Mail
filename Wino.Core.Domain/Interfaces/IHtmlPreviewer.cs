namespace Wino.Domain.Interfaces
{
    public interface IHtmlPreviewer
    {
        /// <summary>
        /// Returns a preview of the HTML content.
        /// </summary>
        /// <param name="htmlContent">HTML content</param>
        string GetHtmlPreview(string htmlContent);
    }
}
