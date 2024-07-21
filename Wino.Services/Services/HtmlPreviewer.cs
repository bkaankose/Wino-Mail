using Wino.Domain.Interfaces;
using Wino.Services.Extensions;

namespace Wino.Services.Services
{
    public class HtmlPreviewer : IHtmlPreviewer
    {
        public string GetHtmlPreview(string htmlContent) => HtmlAgilityPackExtensions.GetPreviewText(htmlContent);
    }
}
