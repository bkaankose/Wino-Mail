using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.WhatsNew;

/// <summary>One feature card in the What's New window.</summary>
public class WhatsNewFeature
{
    /// <summary>File name of the illustration under Assets\WhatsNew, e.g. "colorful-icon-style.png".</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public string ImageUri => string.IsNullOrWhiteSpace(Image)
        ? string.Empty
        : $"ms-appx:///Assets/WhatsNew/{Image}";

    [JsonIgnore]
    public string AccessibilityName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title))
                return Description ?? string.Empty;

            if (string.IsNullOrWhiteSpace(Description))
                return Title;

            return $"{Title}. {Description}";
        }
    }
}
