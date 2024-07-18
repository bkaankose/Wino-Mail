using Newtonsoft.Json;

namespace Wino.Core.Domain.Models.Reader
{
    /// <summary>
    /// Used to pass messages from the webview to the app.
    /// </summary>
    public class WebViewMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("value")]
        public string Value { get; set; }
    }
}
