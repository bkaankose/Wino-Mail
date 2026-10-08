using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public sealed class InitialSynchronizationRangeOption
{
    public InitialSynchronizationRange Range { get; }
    public string DisplayText { get; }

    public bool IsEverything => Range == InitialSynchronizationRange.Everything;

    public InitialSynchronizationRangeOption(InitialSynchronizationRange range, string displayText)
    {
        Range = range;
        DisplayText = displayText;
    }
}
