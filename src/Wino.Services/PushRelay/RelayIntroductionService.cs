#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Services.PushRelay;

/// <summary>
/// The sync method preference being unset while accounts exist is the whole "existing user" test: nothing else in the
/// app records the previously installed version reliably. Fresh installs write the preference before any account exists.
/// </summary>
public sealed class RelayIntroductionService : IRelayIntroductionService
{
    private readonly IPreferencesService _preferences;
    private readonly IConfigurationService _configuration;
    private readonly IAccountService _accounts;
    private readonly IPlatformCapabilities _capabilities;
    private readonly Func<IPushRelayRegistrationService> _pushRelay;
    private readonly Func<IAppPolicyAcceptanceSyncService> _policySync;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<RelayIntroductionService>();

    public RelayIntroductionService(IPreferencesService preferences, IConfigurationService configuration, IAccountService accounts,
        IPlatformCapabilities capabilities, Func<IPushRelayRegistrationService> pushRelay, Func<IAppPolicyAcceptanceSyncService> policySync)
    {
        _preferences = preferences;
        _configuration = configuration;
        _accounts = accounts;
        _capabilities = capabilities;
        _pushRelay = pushRelay;
        _policySync = policySync;
    }

    private bool HasChoice => SettingAvailability.IsAvailable(PlatformSetting.SyncMethod, _capabilities);

    public bool IsPolicyAcceptanceRequired
        => HasChoice && _configuration.Get(PushRelayDefaults.PolicyAcceptedVersionConfigurationKey, 0) < PushRelayDefaults.PolicyVersion;

    public async Task<bool> IsIntroductionPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!HasChoice)
        {
            // Mobile: relay only, the store policy covers it. Record the version so a later desktop restore never re-prompts.
            if (!_preferences.IsSyncMethodConfigured)
                _preferences.SyncMethod = SyncMethod.Relay;
            RecordPolicyIfMissing();
            return false;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_preferences.IsSyncMethodConfigured)
                return _configuration.Get(PushRelayDefaults.IntroductionPendingConfigurationKey, false);

            var accounts = await _accounts.GetAccountsAsync().ConfigureAwait(false);
            if (accounts is { Count: > 0 })
            {
                // Upgraded install: nothing changes until the user chooses.
                _preferences.SyncMethod = SyncMethod.Timer;
                _configuration.Set(PushRelayDefaults.IntroductionPendingConfigurationKey, true);
                _logger.Information("Existing accounts found with no sync method; keeping timer-based sync and queuing the relay introduction.");
                return true;
            }

            // Fresh install: relay-based sync from the start, policy accepted through the store listing.
            _preferences.SyncMethod = SyncMethod.Relay;
            RecordPolicyIfMissing();
            _configuration.Set(PushRelayDefaults.IntroductionPendingConfigurationKey, false);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void AcceptPolicy() => RecordPolicy(AppPolicyAcceptanceSources.Explicit);

    public void DismissIntroduction() => _configuration.Set(PushRelayDefaults.IntroductionPendingConfigurationKey, false);

    public void CompleteWithTimer()
    {
        if (_preferences.SyncMethod != SyncMethod.Timer)
            _preferences.SyncMethod = SyncMethod.Timer;
        _configuration.Set(PushRelayDefaults.IntroductionPendingConfigurationKey, false);
    }

    public async Task CompleteWithRelayAsync(CancellationToken cancellationToken = default)
    {
        AcceptPolicy();
        _configuration.Set(PushRelayDefaults.IntroductionPendingConfigurationKey, false);
        if (_preferences.SyncMethod != SyncMethod.Relay)
            _preferences.SyncMethod = SyncMethod.Relay;

        // The preference change already queues a pass; this waits for it so the caller can report the outcome.
        try { await _pushRelay().SyncNowAsync(PushRelaySyncReason.OptInChanged, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "The first relay registration after the introduction did not finish; the service retries on its own.");
        }
    }

    private void RecordPolicyIfMissing()
    {
        if (_configuration.Get(PushRelayDefaults.PolicyAcceptedVersionConfigurationKey, 0) < PushRelayDefaults.PolicyVersion)
            RecordPolicy(AppPolicyAcceptanceSources.StoreInstall);
    }

    /// <summary>Writes the per-device record, then lets the sync service copy it to the Wino Account in the background.</summary>
    private void RecordPolicy(string source)
    {
        _configuration.Set(PushRelayDefaults.PolicyAcceptedVersionConfigurationKey, PushRelayDefaults.PolicyVersion);
        try { _policySync().NotifyLocalAcceptance(source); }
        catch (Exception ex)
        {
            _logger.Information(ex, "The app policy acceptance could not be queued for the Wino Account; startup retries it.");
        }
    }
}
