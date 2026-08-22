#nullable enable
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Carries the coverage editor's result back to the page that opened it.
/// </summary>
/// <remarks>
/// Back navigation replays the original navigation parameter, so a frame cannot hand a result to
/// the page it returns to. This one-slot mailbox closes that gap: the editor drops its result in
/// before requesting back navigation, and the management page takes it in
/// <c>OnNavigatedTo</c> when the navigation mode is Back.
/// </remarks>
public interface IIntelligenceCoverageHandoff
{
    void Publish(IntelligenceCoverageResult result);

    /// <summary>
    /// Takes the pending result and clears the slot, so a later back navigation that the editor
    /// did not cause cannot re-apply a stale decision.
    /// </summary>
    bool TryTake(out IntelligenceCoverageResult? result);
}
