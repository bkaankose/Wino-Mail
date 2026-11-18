#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.AppLock;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The application lock: a PIN, optionally with the platform's biometric prompt, asked once per main window.
/// The configuration lives in the platform secure store only, so it is never part of a backup or settings sync.
/// </summary>
public interface IAppLockService
{
    /// <summary>False when the platform has no secure storage or no lock prompt; the lock cannot be turned on then.</summary>
    bool IsSupported { get; }

    bool IsEnabled { get; }

    /// <summary>True while the lock is on and the current main window has not been unlocked.</summary>
    bool IsLocked { get; }

    /// <summary>How the lock is opened; null while the lock is off.</summary>
    AppLockMethod? Method { get; }

    bool IsBiometricEnabled { get; }

    /// <summary>Raised on any thread when the lock is turned on or off, or the session locks or unlocks.</summary>
    event EventHandler? StateChanged;

    Task<bool> IsBiometricAvailableAsync();

    /// <summary>
    /// Locks with the app's own PIN, also when the lock is already on with the other method. Returns false, changing
    /// nothing, when the PIN is invalid or secure storage fails.
    /// </summary>
    bool EnableWithPin(string pin);

    /// <summary>Locks with the platform biometric prompt and no app PIN. Returns false when secure storage fails.</summary>
    bool EnableWithBiometric();

    void Disable();

    /// <summary>True when the lock is on and notifications are created without action buttons.</summary>
    bool AreNotificationsReadOnly { get; }

    /// <summary>Returns false when the lock is off or secure storage fails.</summary>
    bool SetNotificationsReadOnly(bool isReadOnly);

    /// <summary>True while user actions must be refused: the session is locked and no action scope is open.</summary>
    bool AreActionsRefused { get; }

    /// <summary>
    /// Lets the calling asynchronous flow queue actions while the session is locked, for a notification button the
    /// user was allowed to keep. Dispose it when the action is queued.
    /// </summary>
    IDisposable AllowActionsWhileLocked();

    bool ChangePin(string pin);

    /// <summary>Checks a PIN and counts wrong attempts. It does not change the session.</summary>
    AppLockPinVerification VerifyPin(string pin);

    /// <summary>Shows the platform biometric prompt. It does not change the session.</summary>
    Task<bool> VerifyBiometricAsync();

    /// <summary>Asks the user to prove who they are before a sensitive step. True when the lock is off.</summary>
    Task<bool> RequestVerificationAsync();

    /// <summary>Unlocks the current main window's session after a successful verification.</summary>
    void UnlockSession();

    /// <summary>Ends the session; the next main window asks again.</summary>
    void LockSession();
}
