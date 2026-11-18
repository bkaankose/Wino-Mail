using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Hosting;

/// <summary>
/// The app lock of a host with no screen: a Wino Mail PIN only, off until a script turns it on, with no biometric
/// prompt. The verification prompt is a dialog: it is recorded and dismissed unless a script queued the PIN to type
/// with <see cref="WinoHost.AnswerNextDialog{T}"/> under <c>"PromptVerificationAsync"</c>, and that PIN
/// is checked by the real lock service.
/// </summary>
internal sealed class HostAppLockPlatform(IServiceProvider services, HostInteractionLog log, HostDialogAnswers answers) : IAppLockPlatform
{
    public bool IsAvailable => true;

    public Task<bool> IsBiometricAvailableAsync() => Task.FromResult(false);

    public Task<bool> VerifyBiometricAsync()
    {
        log.Record(HostInteractionKind.Dialog, nameof(VerifyBiometricAsync), null, "unavailable");
        return Task.FromResult(false);
    }

    public Task<bool> PromptVerificationAsync()
    {
        if (!answers.TryTake(nameof(PromptVerificationAsync), out string? pin))
        {
            log.Record(HostInteractionKind.Dialog, nameof(PromptVerificationAsync), null, "dismissed");
            return Task.FromResult(false);
        }

        // Resolved here: the lock service takes this platform in its constructor.
        var verification = services.GetRequiredService<IAppLockService>().VerifyPin(pin);
        log.Record(HostInteractionKind.Dialog, nameof(PromptVerificationAsync), null, $"answered with a PIN: {verification.Status}");
        return Task.FromResult(verification.IsSuccess);
    }
}
