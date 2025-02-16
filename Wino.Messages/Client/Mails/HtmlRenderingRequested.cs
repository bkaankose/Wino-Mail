namespace Wino.Messaging.Client.Mails
{
    /// <summary>
    /// When existing a new html is requested to be rendered due to mail selection or signature.
    /// </summary>
    /// <param name="HtmlBody">HTML to render in WebView2.</param>
    public record HtmlRenderingRequested(string HtmlBody);
}
