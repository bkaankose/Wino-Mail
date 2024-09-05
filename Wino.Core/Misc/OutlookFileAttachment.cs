using System.Text.Json.Serialization;

namespace Wino.Core.Misc
{
    public class OutlookFileAttachment
    {
        [JsonPropertyName("@odata.type")]
        public string OdataType { get; } = "#microsoft.graph.fileAttachment";

        [JsonPropertyName("name")]
        public string FileName { get; set; }

        [JsonPropertyName("contentBytes")]
        public string Base64EncodedContentBytes { get; set; }

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; }

        [JsonPropertyName("contentId")]
        public string ContentId { get; set; }

        [JsonPropertyName("isInline")]
        public bool IsInline { get; set; }
    }
}
