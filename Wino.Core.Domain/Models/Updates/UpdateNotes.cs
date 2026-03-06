using System.Collections.Generic;
namespace Wino.Core.Domain.Models.Updates;

public class UpdateNotes
{
    public List<UpdateNoteSection> Sections { get; set; } = [];
}
