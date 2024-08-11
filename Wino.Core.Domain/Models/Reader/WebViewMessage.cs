using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.Reader
{
    /// <summary>
    /// Used to pass messages from the webview to the app.
    /// </summary>
    public class WebViewMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }
    }
}
