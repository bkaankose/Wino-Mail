#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;

namespace Wino.Services.Security;

public sealed class AppLockService : IAppLockService
{
    // Names an existing secure item; never change them for an installed app.
    private const string Scope = "app-lock";
    private const string Account = "configuration-v1";

    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int Iterations = 200_000;
    private const int FreeAttempts = 5;
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromSeconds(30);

    private readonly ISecureItemStore _store;
    private readonly IAppLockPlatform _platform;
    private readonly Lock _gate = new();
    private readonly AsyncLocal<int> _actionScopes = new();

    private AppLockConfiguration? _configuration;
    private bool _isSessionUnlocked;

    public AppLockService(ISecureItemStore store, IAppLockPlatform platform)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _configuration = _platform.IsAvailable ? Load() : null;
    }

    public event EventHandler? StateChanged;

    public bool IsSupported => _platform.IsAvailable;

    public bool IsEnabled
    {
        get { lock (_gate) return _configuration is not null; }
    }

    public bool IsLocked
    {
        get { lock (_gate) return _configuration is not null && !_isSessionUnlocked; }
    }

    public AppLockMethod? Method
    {
        get { lock (_gate) return _configuration?.Method; }
    }

    public bool IsBiometricEnabled => Method == AppLockMethod.Biometric;

    public bool AreNotificationsReadOnly
    {
        get { lock (_gate) return _configuration?.NotificationsReadOnly == true; }
    }

    public bool SetNotificationsReadOnly(bool isReadOnly)
    {
        lock (_gate)
        {
            if (_configuration is null) return false;
            if (_configuration.NotificationsReadOnly == isReadOnly) return true;

            var configuration = _configuration with { NotificationsReadOnly = isReadOnly };
            if (!Save(configuration)) return false;

            _configuration = configuration;
        }

        RaiseStateChanged();
        return true;
    }

    public bool AreActionsRefused => IsLocked && _actionScopes.Value == 0;

    public IDisposable AllowActionsWhileLocked()
    {
        _actionScopes.Value++;
        return new ActionScope(_actionScopes);
    }

    private sealed class ActionScope(AsyncLocal<int> scopes) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            scopes.Value--;
        }
    }

    public Task<bool> IsBiometricAvailableAsync()
        => _platform.IsBiometricAvailableAsync();

    public bool EnableWithPin(string pin)
        => IsSupported && AppLockPinRules.IsValid(pin) && Apply(CreatePinConfiguration(pin));

    public bool EnableWithBiometric()
        => IsSupported && Apply(new AppLockConfiguration(AppLockMethod.Biometric, null, null, 0, 0, null));

    private bool Apply(AppLockConfiguration configuration)
    {
        lock (_gate)
        {
            configuration = configuration with { NotificationsReadOnly = _configuration?.NotificationsReadOnly == true };
            if (!Save(configuration)) return false;

            _configuration = configuration;
            // The person who just chose the method is at the keyboard; do not lock them out of the window they are in.
            _isSessionUnlocked = true;
        }

        RaiseStateChanged();
        return true;
    }

    public void Disable()
    {
        lock (_gate)
        {
            if (_configuration is null) return;

            try
            {
                _store.Delete(Scope, Account);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Error(error, "The app lock configuration could not be deleted; the lock stays on.");
                return;
            }

            _configuration = null;
            _isSessionUnlocked = false;
        }

        RaiseStateChanged();
    }

    public bool ChangePin(string pin)
    {
        if (!AppLockPinRules.IsValid(pin)) return false;

        lock (_gate)
        {
            if (_configuration?.Method != AppLockMethod.Pin) return false;

            var configuration = CreatePinConfiguration(pin) with { NotificationsReadOnly = _configuration.NotificationsReadOnly };
            if (!Save(configuration)) return false;

            _configuration = configuration;
        }

        return true;
    }

    public AppLockPinVerification VerifyPin(string pin)
    {
        lock (_gate)
        {
            if (_configuration is null) return new(AppLockPinVerificationStatus.Success, TimeSpan.Zero);
            if (_configuration.Salt is null || _configuration.Hash is null) return new(AppLockPinVerificationStatus.Wrong, TimeSpan.Zero);

            var now = DateTimeOffset.UtcNow;
            if (_configuration.RetryNotBefore is { } retryNotBefore && retryNotBefore > now)
                return new(AppLockPinVerificationStatus.Throttled, retryNotBefore - now);

            var isMatch = pin is not null && CryptographicOperations.FixedTimeEquals(
                Hash(pin, _configuration.Salt, _configuration.Iterations),
                _configuration.Hash);

            if (isMatch)
            {
                if (_configuration.FailedAttempts != 0 || _configuration.RetryNotBefore is not null)
                    Persist(_configuration with { FailedAttempts = 0, RetryNotBefore = null });

                return new(AppLockPinVerificationStatus.Success, TimeSpan.Zero);
            }

            var failedAttempts = _configuration.FailedAttempts + 1;
            var isThrottled = failedAttempts >= FreeAttempts;
            Persist(_configuration with
            {
                FailedAttempts = failedAttempts,
                RetryNotBefore = isThrottled ? now + ThrottleDelay : null
            });

            return isThrottled
                ? new(AppLockPinVerificationStatus.Throttled, ThrottleDelay)
                : new(AppLockPinVerificationStatus.Wrong, TimeSpan.Zero);
        }
    }

    public async Task<bool> VerifyBiometricAsync()
    {
        if (!IsBiometricEnabled) return false;

        return await _platform.VerifyBiometricAsync().ConfigureAwait(false);
    }

    public async Task<bool> RequestVerificationAsync()
    {
        var method = Method;
        if (method is null) return true;

        return method == AppLockMethod.Biometric
            ? await _platform.VerifyBiometricAsync().ConfigureAwait(false)
            : await _platform.PromptVerificationAsync().ConfigureAwait(false);
    }

    public void UnlockSession()
    {
        lock (_gate)
        {
            if (_configuration is null || _isSessionUnlocked) return;
            _isSessionUnlocked = true;
        }

        RaiseStateChanged();
    }

    public void LockSession()
    {
        lock (_gate)
        {
            if (_configuration is null || !_isSessionUnlocked) return;
            _isSessionUnlocked = false;
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static AppLockConfiguration CreatePinConfiguration(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        return new AppLockConfiguration(AppLockMethod.Pin, salt, Hash(pin, salt, Iterations), Iterations, 0, null);
    }

    private static byte[] Hash(string pin, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashLength);

    // The attempt counter is best effort: a failed write must not turn a correct PIN into a refusal.
    private void Persist(AppLockConfiguration configuration)
    {
        _configuration = configuration;
        Save(configuration);
    }

    private bool Save(AppLockConfiguration configuration)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, AppLockJsonContext.Default.AppLockConfiguration);
            _store.Write(Scope, Account, bytes, SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);

            // Turning the lock on is only allowed when the secure store hands the same item back.
            var stored = _store.Read(Scope, Account);
            return stored is not null && stored.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Error(error, "The app lock configuration could not be stored securely.");
            return false;
        }
    }

    private AppLockConfiguration? Load()
    {
        try
        {
            var bytes = _store.Read(Scope, Account);
            if (bytes is null || bytes.Length == 0) return null;

            var configuration = JsonSerializer.Deserialize(bytes, AppLockJsonContext.Default.AppLockConfiguration);
            if (configuration is null
                || !Enum.IsDefined(configuration.Method)
                || (configuration.Method == AppLockMethod.Pin
                    && (configuration.Salt is not { Length: > 0 }
                        || configuration.Hash is not { Length: > 0 }
                        || configuration.Iterations <= 0)))
            {
                Log.Warning("The stored app lock configuration is incomplete; the lock is off.");
                return null;
            }

            return configuration;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // An item this Windows user can no longer decrypt cannot be verified against, so the lock is off rather than permanent.
            Log.Error(error, "The stored app lock configuration is unreadable; the lock is off.");
            return null;
        }
    }
}

internal sealed record AppLockConfiguration(
    AppLockMethod Method,
    byte[]? Salt,
    byte[]? Hash,
    int Iterations,
    int FailedAttempts,
    DateTimeOffset? RetryNotBefore,
    bool NotificationsReadOnly = false);

[JsonSerializable(typeof(AppLockConfiguration))]
internal sealed partial class AppLockJsonContext : JsonSerializerContext;
