using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.Updates;

public class UpdateNoteSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("imageWidth")]
    public double? ImageWidth { get; set; }

    [JsonPropertyName("imageHeight")]
    public double? ImageHeight { get; set; }

    /// <summary>Gets the image width for binding, returning NaN for auto-sizing when not specified.</summary>
    public double ActualImageWidth => ImageWidth ?? double.NaN;

    /// <summary>Gets the image height for binding, returning NaN for auto-sizing when not specified.</summary>
    public double ActualImageHeight => ImageHeight ?? double.NaN;

    public string AccessibilityName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                return Description ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(Description))
            {
                return Title;
            }

            return $"{Title}. {Description}";
        }
    }
}
