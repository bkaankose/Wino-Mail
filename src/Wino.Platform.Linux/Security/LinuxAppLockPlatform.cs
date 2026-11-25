using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Security;

/// <summary>
/// The app lock on Linux: a Wino Mail PIN only (no biometric prompt). This project has no views, so the head sets
/// <see cref="Prompt"/> to its PIN dialog once the window exists; the prompt always runs on the GTK main thread.
/// </summary>
public sealed class LinuxAppLockPlatform(IDispatcher dispatcher) : IAppLockPlatform
{
    public bool IsAvailable => true;

    public Func<Task<bool>>? Prompt { get; set; }

    public Task<bool> IsBiometricAvailableAsync() => Task.FromResult(false);

    public Task<bool> VerifyBiometricAsync() => Task.FromResult(false);

    public async Task<bool> PromptVerificationAsync()
    {
        if (Prompt is not { } prompt)
        {
            Serilog.Log.Warning("The app lock verification was refused because no window can show the PIN prompt.");
            return false;
        }
        Task<bool>? shown = null;
        await dispatcher.ExecuteOnUIThread(() => shown = prompt());
        try { return shown is not null && await shown; }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The app lock verification prompt failed.");
            return false;
        }
    }
}
