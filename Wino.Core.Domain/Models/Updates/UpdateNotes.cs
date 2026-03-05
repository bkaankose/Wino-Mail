using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.Updates;

public class UpdateNotes
{
    [JsonPropertyName("hasPendingMigrations")]
    public bool HasPendingMigrations { get; set; }

    [JsonPropertyName("migration")]
    public UpdateMigration Migration { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<UpdateNoteSection> Sections { get; set; } = [];
}
