#nullable enable
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Platform;

/// <summary>
/// For hosts that have no app lock prompt yet (macOS, iOS, Android). Their platform capabilities report
/// <c>AppLock: false</c>, so the settings page is not offered, and the lock service never turns the lock on.
/// </summary>
public sealed class UnavailableAppLockPlatform : IAppLockPlatform
{
    public bool IsAvailable => false;

    public Task<bool> IsBiometricAvailableAsync() => Task.FromResult(false);

    public Task<bool> VerifyBiometricAsync() => Task.FromResult(false);

    public Task<bool> PromptVerificationAsync() => Task.FromResult(false);
}
