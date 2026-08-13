using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace Wino.Core.Domain.Models.Updates;

public class UpdateNotes
{
    [JsonPropertyName("sections")]
    public List<UpdateNoteSection> Sections { get; set; } = [];
}
