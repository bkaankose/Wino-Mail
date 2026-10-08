using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.Reader;

public class ImageInfo
{
    [JsonPropertyName("data")]
    public string Data { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}
