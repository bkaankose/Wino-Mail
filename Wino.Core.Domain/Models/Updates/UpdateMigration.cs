using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.Updates;

public class UpdateMigration
{
    [JsonPropertyName("titleKey")]
    public string TitleKey { get; set; } = string.Empty;

    [JsonPropertyName("descriptionKey")]
    public string DescriptionKey { get; set; } = string.Empty;
}
