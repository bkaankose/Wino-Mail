using System.Collections.Generic;
using MimeKit;

// Lives in Wino.Services (companion-only) because it carries MimeKit types;
// the namespace is kept so companion consumers stay unchanged. The UI works
// with the serializable MailRenderInfo instead.
namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// Final model to be passed to renderer page.
/// Data here are created based on rendering settings.
/// </summary>
public class MailRenderModel
{
    public string RenderHtml { get; }
    public string AccessibleText { get; }
    public MailRenderingOptions MailRenderingOptions { get; }
    public List<MimePart> Attachments { get; set; } = [];

    public UnsubscribeInfo UnsubscribeInfo { get; set; }

    /// <summary>
    /// S/MIME state of the rendered message. Computed by the companion
    /// (ISmimeService.PrepareSmimeRenderAsync) and attached by the rendering view model;
    /// null when the message carries no S/MIME layers.
    /// </summary>
    public SmimeRenderInfo SmimeInfo { get; set; }

    public MailRenderModel(string renderHtml, MailRenderingOptions mailRenderingOptions = null, string accessibleText = "")
    {
        RenderHtml = renderHtml;
        MailRenderingOptions = mailRenderingOptions;
        AccessibleText = accessibleText ?? string.Empty;
    }
}
