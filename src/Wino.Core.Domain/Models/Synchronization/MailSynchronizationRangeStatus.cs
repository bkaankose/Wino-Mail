using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

public sealed record MailSynchronizationRangeStatus(
    bool IsSupported,
    bool IsInitialSynchronizationCompleted,
    bool IsChangeRunning,
    InitialSynchronizationRange CurrentRange)
{
    public static MailSynchronizationRangeStatus Unsupported { get; } = new(false, false, false, InitialSynchronizationRange.Everything);
}
