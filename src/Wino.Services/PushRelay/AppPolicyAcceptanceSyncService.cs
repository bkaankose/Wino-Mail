#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Mail.Api.Contracts.Users;
using Wino.Messaging.UI;

namespace Wino.Services.PushRelay;

/// <summary>
/// Syncs the app privacy policy acceptance between this device (<see cref="PushRelayDefaults.PolicyAcceptedVersionConfigurationKey"/>)
/// and the signed-in Wino Account. Runs on local acceptance, sign-in, profile changes and startup. An upload refreshes the
/// profile; the refreshed profile then compares equal, so a pass never triggers another upload.
/// </summary>
public sealed class AppPolicyAcceptanceSyncService : IAppPolicyAcceptanceSyncService,
    IRecipient<WinoAccountSignedInMessage>, IRecipient<WinoAccountProfileUpdatedMessage>
{
    private readonly IConfigurationService _configuration;
    private readonly IWinoAccountProfileService _profiles;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly IPushRelayDeviceInfo? _deviceInfo;
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<AppPolicyAcceptanceSyncService>();
    private bool _missingPlatformLogged;

    public AppPolicyAcceptanceSyncService(
        IConfigurationService configuration,
        IWinoAccountProfileService profiles,
        IWinoAccountApiClient apiClient,
        IPushRelayDeviceInfo? deviceInfo = null,
        IMessenger? messenger = null)
    {
        _configuration = configuration;
        _profiles = profiles;
        _apiClient = apiClient;
        _deviceInfo = deviceInfo;

        if (messenger is not null)
        {
            messenger.Register<WinoAccountSignedInMessage>(this);
            messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        }
    }

    public void NotifyLocalAcceptance(string source)
    {
        _configuration.Set(PushRelayDefaults.PolicyAcceptanceSourceConfigurationKey, source);
        StartReconcile();
    }

    public void Receive(WinoAccountSignedInMessage message) => StartReconcile();

    public void Receive(WinoAccountProfileUpdatedMessage message) => StartReconcile();

    /// <summary>Messages arrive inside the profile service's commit; the pass runs off that call stack.</summary>
    private void StartReconcile() => _ = Task.Run(() => ReconcileAsync());

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _passLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "App policy acceptance sync did not finish; it retries at the next sync point.");
        }
        finally
        {
            _passLock.Release();
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var account = await _profiles.GetActiveAccountAsync().ConfigureAwait(false);
        if (account is null)
            return;

        var localVersion = _configuration.Get(PushRelayDefaults.PolicyAcceptedVersionConfigurationKey, 0);
        var accountVersion = account.AcceptedAppPolicyVersion ?? 0;

        if (accountVersion > localVersion)
        {
            _configuration.Set(PushRelayDefaults.PolicyAcceptedVersionConfigurationKey, accountVersion);
            _logger.Information("Adopted app policy version {Version} accepted on the Wino Account.", accountVersion);
            return;
        }

        if (localVersion <= accountVersion)
            return;

        var platform = DescribePlatform();
        if (platform is null)
            return;

        var source = _configuration.Get(PushRelayDefaults.PolicyAcceptanceSourceConfigurationKey, AppPolicyAcceptanceSources.StoreInstall);
        if (string.IsNullOrWhiteSpace(source))
            source = AppPolicyAcceptanceSources.StoreInstall;

        var response = await _apiClient.AcceptAppPolicyAsync(localVersion, source, platform, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result is null)
        {
            _logger.Information("Uploading app policy version {Version} to the Wino Account failed ({ErrorCode}); keeping the local record.",
                localVersion, response.ErrorCode);
            return;
        }

        _logger.Information("Recorded app policy version {Version} ({Source}) on the Wino Account.", localVersion, source);

        // Converges the stored row; an equal profile publishes nothing, so this does not start another upload.
        var refresh = await _profiles.RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
        if (!refresh.IsSuccess)
            _logger.Information("Refreshing the Wino Account after the app policy upload failed ({ErrorCode}).", refresh.ErrorCode);
    }

    private string? DescribePlatform()
    {
        string? platform = null;
        try
        {
            platform = _deviceInfo?.Describe().Platform;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "The device description failed.");
        }

        if (string.IsNullOrWhiteSpace(platform))
        {
            if (!_missingPlatformLogged)
            {
                _missingPlatformLogged = true;
                _logger.Information("No relay device info on this platform; the app policy acceptance stays local.");
            }

            return null;
        }

        return platform;
    }
}
