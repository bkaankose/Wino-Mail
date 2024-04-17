using System.Collections.Generic;
using MimeKit;

namespace Wino.Core.Domain.Models.Reader
{
    /// <summary>
    /// Final model to be passed to renderer page.
    /// Data here are created based on rendering settings.
    /// </summary>
    public class MailRenderModel
    {
        public string RenderHtml { get; }
        public MailRenderingOptions MailRenderingOptions { get; }
        public List<MimePart> Attachments { get; set; } = new List<MimePart>();

        public string UnsubscribeLink { get; set; }
        public bool CanUnsubscribe => !string.IsNullOrEmpty(UnsubscribeLink);

        public MailRenderModel(string renderHtml, MailRenderingOptions mailRenderingOptions = null)
        {
            RenderHtml = renderHtml;
            MailRenderingOptions = mailRenderingOptions;
        }
    }
}
