using System.Threading.Tasks;
using Wino.Core.Domain.Models.Updates;
using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces;

public interface IUpdateManager
{
    /// <summary>Loads and parses the update notes for the current version from the bundled asset file.</summary>
    Task<UpdateNotes> GetLatestUpdateNotesAsync();

    /// <summary>Loads and parses the app feature highlights from the bundled asset file.</summary>
    Task<List<UpdateNoteSection>> GetFeaturesAsync();

    /// <summary>Returns true if the current version's update notes have not yet been shown to the user.</summary>
    bool ShouldShowUpdateNotes();

    /// <summary>Stores a flag in local settings indicating the update notes for the current version have been seen.</summary>
    void MarkUpdateNotesAsSeen();

}
