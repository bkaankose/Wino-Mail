using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.Updates;

public class UpdateNotes
{
    [JsonPropertyName("hasMigrations")]
    public bool HasMigrations { get; set; }

    [JsonPropertyName("sections")]
    public List<UpdateNoteSection> Sections { get; set; } = [];
}
