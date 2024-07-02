namespace Wino.Core.Domain.Models.Requests
{
    // Used to pass messages from the webview to the app.
    public class WebViewMessage
    {
        public string type { get; set; }
        public string value { get; set; }
    }
}
