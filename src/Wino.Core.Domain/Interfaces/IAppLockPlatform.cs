#nullable enable
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>The platform half of the application lock. The lock can be turned on only where it is available.</summary>
public interface IAppLockPlatform
{
    /// <summary>False on a platform that has no lock prompt yet; the lock is then never on.</summary>
    bool IsAvailable { get; }

    Task<bool> IsBiometricAvailableAsync();

    Task<bool> VerifyBiometricAsync();

    /// <summary>Shows a prompt that verifies the app PIN through <see cref="IAppLockService"/>.</summary>
    Task<bool> PromptVerificationAsync();
}
