using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Whether the single-key shortcuts of <see cref="LinuxKeymap"/> (j, k, r, e, ...) act. The user's choice is the
/// head-local key <see cref="Key"/>; without one they are on when <paramref name="defaultOn"/> says so (a tiling desktop).
/// </summary>
internal sealed class SingleKeyShortcuts(IConfigurationService configuration, Func<bool> defaultOn)
{
    public const string Key = "LinuxSingleKeyShortcuts";

    public bool IsEnabled => configuration.Contains(Key) ? configuration.Get(Key, false) : defaultOn();

    public bool HasChoice => configuration.Contains(Key);

    public event Action? Changed;

    public void Set(bool enabled)
    {
        if (HasChoice && IsEnabled == enabled) return;
        configuration.Set(Key, enabled);
        Changed?.Invoke();
    }

    /// <summary>Back to the desktop's default.</summary>
    public void ClearChoice()
    {
        if (!configuration.Remove(Key)) return;
        Changed?.Invoke();
    }
}
