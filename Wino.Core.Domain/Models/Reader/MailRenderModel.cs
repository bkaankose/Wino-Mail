using System.Collections.Generic;
using MimeKit;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// Final model to be passed to renderer page.
/// Data here are created based on rendering settings.
/// </summary>
public class MailRenderModel
{
    public string RenderHtml { get; }
    public MailRenderingOptions MailRenderingOptions { get; }
    public List<MimePart> Attachments { get; set; } = [];

    public UnsubscribeInfo UnsubscribeInfo { get; set; }

    public MailRenderModel(string renderHtml, MailRenderingOptions mailRenderingOptions = null)
    {
        RenderHtml = renderHtml;
        MailRenderingOptions = mailRenderingOptions;
    }
}

public class UnsubscribeInfo
{
    public string HttpLink { get; set; }
    public string MailToLink { get; set; }
    public bool IsOneClick { get; set; }
    public bool CanUnsubscribe => HttpLink != null || MailToLink != null;
}
