using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.WhatsNew;

namespace Wino.Core.Domain.Interfaces;

public interface IWhatsNewService
{
    /// <summary>Loads every bundled release note, newest version first. Invalid files are skipped.</summary>
    Task<IReadOnlyList<WhatsNewRelease>> GetReleasesAsync();

    /// <summary>
    /// True when notes exist for the running package version and the What's New window has not
    /// been opened for that version yet.
    /// </summary>
    Task<bool> ShouldShowShellEntryAsync();

    /// <summary>Records that the What's New window was opened for the running package version.</summary>
    void MarkOpenedForCurrentVersion();
}
