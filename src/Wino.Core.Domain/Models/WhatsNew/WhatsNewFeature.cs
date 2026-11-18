using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.WhatsNew;

/// <summary>One feature card in the What's New window.</summary>
public class WhatsNewFeature
{
    /// <summary>File name of the illustration under Assets\WhatsNew, e.g. "colorful-icon-style.png".</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;

    /// <summary>Optional animated GIF of the same scene. Only Windows plays it; the other apps keep showing <see cref="Image"/>.</summary>
    [JsonPropertyName("animation")]
    public string Animation { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public string ImageUri
    {
        get
        {
            var file = string.IsNullOrWhiteSpace(Animation) ? Image : Animation;
            return string.IsNullOrWhiteSpace(file) ? string.Empty : $"ms-appx:///Assets/WhatsNew/{file}";
        }
    }

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
