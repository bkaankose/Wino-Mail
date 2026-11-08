#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

/// <summary>Who may use voice control: it needs a Wino Account with Wino Intelligence.</summary>
public enum VoiceControlAccess
{
    SignedOut,
    NotSubscribed,
    Available
}

/// <summary>A microphone choice. An empty <see cref="Id"/> follows the system default.</summary>
public sealed record MicrophoneOption(string Id, string DisplayName);

/// <summary>
/// Settings › Voice Control. Without Wino Intelligence it only introduces the feature; with it, it chooses the
/// microphone, the bubble position and whether voice control is on. The global shortcut stays in the Windows
/// page because it registers a system hotkey there.
/// </summary>
public partial class VoiceControlSettingsPageViewModel : CoreBaseViewModel,
    IRecipient<PreferenceChangedMessage>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IRecipient<VoiceModeChanged>
{
    private readonly IMicrophoneService _microphoneService;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoAccountProfileService _profileService;
    private readonly IWinoBillingService _billingService;
    private readonly IVoiceBrainService _voiceBrain;
    private bool _isChangingMode;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private CancellationTokenSource? _checkoutWatch;
    private IDisposable? _levelMonitor;
    private CancellationTokenSource? _monitorCancellation;
    private bool _isActive;
    private bool _isWatchingMicrophones;
    private bool _isLoadingMicrophones;

    public VoiceControlSettingsPageViewModel(
        IPreferencesService preferencesService,
        IMicrophoneService microphoneService,
        IWinoAccountIntelligenceSnapshotService entitlementService,
        IMailDialogService dialogService,
        IWinoAccountProfileService profileService,
        IWinoBillingService billingService,
        IVoiceBrainService voiceBrain)
    {
        PreferencesService = preferencesService;
        _dialogService = dialogService;
        _profileService = profileService;
        _billingService = billingService;
        _microphoneService = microphoneService;
        _entitlementService = entitlementService;
        _voiceBrain = voiceBrain;
        Access = ToAccess(entitlementService.CurrentEntitlement);
    }

    /// <summary>
    /// Personal mode lets the voice planner use what Wino knows about the accounts. It belongs to the Wino Account, so
    /// the switch follows the server; turning it off asks first because the server forgets everything it learned.
    /// </summary>
    public bool IsPersonalMode
    {
        get => _voiceBrain.CurrentMode == VoiceMode.Personal;
        set
        {
            if (_isChangingMode || value == IsPersonalMode)
                return;

            _ = ChangeModeAsync(value ? VoiceMode.Personal : VoiceMode.Private);
        }
    }

    [ObservableProperty]
    public partial bool IsModeChangeFailed { get; set; }

    private async Task ChangeModeAsync(VoiceMode mode)
    {
        _isChangingMode = true;
        try
        {
            await ExecuteUIThread(() => IsModeChangeFailed = false);

            if (mode == VoiceMode.Private)
            {
                var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                    Translator.VoiceBrain_TurnOff_DialogMessage,
                    Translator.VoiceBrain_TurnOff_DialogTitle,
                    Translator.VoiceBrain_TurnOff_DialogConfirm);
                if (!confirmed)
                    return;
            }

            var changed = await _voiceBrain.SetModeAsync(mode).ConfigureAwait(false);
            if (!changed)
                await ExecuteUIThread(() => IsModeChangeFailed = true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not change the voice mode.");
            await ExecuteUIThread(() => IsModeChangeFailed = true);
        }
        finally
        {
            _isChangingMode = false;

            // The switch already moved; raising the change puts it where the server's mode is.
            await ExecuteUIThread(() => OnPropertyChanged(nameof(IsPersonalMode)));
        }
    }

    [RelayCommand]
    private void OpenVoiceBrain() => Navigate(WinoPage.VoiceBrainPage);

    public void Receive(VoiceModeChanged message)
        => _ = ExecuteUIThread(() => OnPropertyChanged(nameof(IsPersonalMode)));

    public IPreferencesService PreferencesService { get; }

    public ObservableCollection<MicrophoneOption> Microphones { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut), nameof(IsNotSubscribed), nameof(IsLocked), nameof(IsAvailable))]
    public partial VoiceControlAccess Access { get; set; }

    public bool IsSignedOut => Access == VoiceControlAccess.SignedOut;
    public bool IsNotSubscribed => Access == VoiceControlAccess.NotSubscribed;
    public bool IsLocked => Access != VoiceControlAccess.Available;
    public bool IsAvailable => Access == VoiceControlAccess.Available;

    [ObservableProperty]
    public partial MicrophoneOption? SelectedMicrophone { get; set; }

    /// <summary>The selected microphone's input level, 0 to 1, while <see cref="IsTestingMicrophone"/>.</summary>
    [ObservableProperty]
    public partial double MicrophoneLevel { get; set; }

    /// <summary>
    /// The microphone is opened only while the user tests it, so visiting the page never asks for microphone access
    /// or turns on the Windows microphone indicator.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTestingMicrophone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMicrophoneProblem), nameof(MicrophoneProblemMessage), nameof(IsMicrophoneAccessDenied))]
    public partial MicrophoneAccess MicrophoneAccess { get; set; } = MicrophoneAccess.Allowed;

    public bool HasMicrophoneProblem => IsAvailable && IsVoiceControlEnabled && MicrophoneAccess != MicrophoneAccess.Allowed;

    public bool IsMicrophoneAccessDenied => MicrophoneAccess == MicrophoneAccess.Denied;

    public string MicrophoneProblemMessage => MicrophoneAccess switch
    {
        MicrophoneAccess.Denied => Translator.VoiceControlSettings_Microphone_AccessDenied,
        MicrophoneAccess.NoDevice => Translator.VoiceControlSettings_Microphone_NoDevice,
        MicrophoneAccess.Unavailable => Translator.VoiceControlSettings_Microphone_Unavailable,
        _ => string.Empty
    };

    public bool IsVoiceControlEnabled
    {
        get => PreferencesService.IsVoiceControlEnabled;
        set
        {
            if (PreferencesService.IsVoiceControlEnabled == value) return;
            PreferencesService.IsVoiceControlEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMicrophoneProblem));
            if (!value)
                IsTestingMicrophone = false;
        }
    }

    /// <summary>
    /// Runs voice plans without Yes. Turning it on asks first; risky steps keep their second confirmation either way.
    /// </summary>
    public bool IsAutoConfirmEnabled
    {
        get => PreferencesService.IsVoiceAutoConfirmEnabled;
        set
        {
            if (PreferencesService.IsVoiceAutoConfirmEnabled == value) return;

            if (!value)
            {
                PreferencesService.IsVoiceAutoConfirmEnabled = false;
                OnPropertyChanged();
                return;
            }

            _ = ConfirmAutoConfirmAsync();
        }
    }

    private async Task ConfirmAutoConfirmAsync()
    {
        var accepted = false;
        try
        {
            accepted = await _dialogService.ShowConfirmationDialogAsync(
                Translator.VoiceControlSettings_AutoConfirm_DialogMessage,
                Translator.VoiceControlSettings_AutoConfirm_DialogTitle,
                Translator.VoiceControlSettings_AutoConfirm_DialogConfirm);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not ask to turn on voice auto-confirm.");
        }

        // The switch already shows "on"; raising the change puts it back when the user declined.
        await ExecuteUIThread(() =>
        {
            if (accepted)
                PreferencesService.IsVoiceAutoConfirmEnabled = true;
            OnPropertyChanged(nameof(IsAutoConfirmEnabled));
        });
    }
    public VoiceBubblePosition SelectedPosition
    {
        get => PreferencesService.VoiceBubblePosition;
        set
        {
            if (PreferencesService.VoiceBubblePosition == value) return;
            PreferencesService.VoiceBubblePosition = value;
            OnPropertyChanged();
        }
    }

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        _isActive = true;
        ApplyAccess(Access);
        _ = RefreshAccessAsync();
    }

    private async Task RefreshModeAsync()
    {
        await _voiceBrain.RefreshModeAsync().ConfigureAwait(false);
        await ExecuteUIThread(() => OnPropertyChanged(nameof(IsPersonalMode)));
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        _isActive = false;
        _checkoutWatch?.Cancel();
        WatchMicrophones(false);
        IsTestingMicrophone = false;
        _ = StopLevelMonitorAsync();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<PreferenceChangedMessage>(this);
        Messenger.Register<WinoIntelligenceEntitlementChanged>(this);
        Messenger.Register<VoiceModeChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<PreferenceChangedMessage>(this);
        Messenger.Unregister<WinoIntelligenceEntitlementChanged>(this);
        Messenger.Unregister<VoiceModeChanged>(this);
    }

    public void Receive(PreferenceChangedMessage message)
    {
        switch (message.PropertyName)
        {
            case nameof(IPreferencesService.IsVoiceControlEnabled):
                _ = ExecuteUIThread(() =>
                {
                    OnPropertyChanged(nameof(IsVoiceControlEnabled));
                    OnPropertyChanged(nameof(HasMicrophoneProblem));
                });
                break;
            case nameof(IPreferencesService.VoiceBubblePosition):
                _ = ExecuteUIThread(() => OnPropertyChanged(nameof(SelectedPosition)));
                break;
        }
    }

    public void Receive(WinoIntelligenceEntitlementChanged message)
        => _ = ExecuteUIThread(() => ApplyAccess(ToAccess(message.Entitlement)));

    partial void OnSelectedMicrophoneChanged(MicrophoneOption? value)
    {
        if (_isLoadingMicrophones || value is null)
            return;

        if (!string.Equals(PreferencesService.VoiceMicrophoneDeviceId, value.Id, StringComparison.Ordinal))
            PreferencesService.VoiceMicrophoneDeviceId = value.Id;

        if (IsTestingMicrophone)
            _ = RestartLevelMonitorAsync();
    }

    partial void OnIsTestingMicrophoneChanged(bool value)
    {
        if (value)
            _ = RestartLevelMonitorAsync();
        else
            _ = StopLevelMonitorAsync();
    }

    [RelayCommand]
    private async Task GetWinoIntelligenceAsync()
    {
        var outcome = await WinoIntelligenceCheckout.StartAsync(_profileService, _dialogService, _billingService, _entitlementService);

        if (outcome == WinoIntelligenceCheckoutOutcome.AlreadySubscribed)
        {
            await RefreshAccessAsync();
            return;
        }

        if (outcome != WinoIntelligenceCheckoutOutcome.CheckoutOpened)
            return;

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      Translator.VoiceControlSettings_CheckoutOpened,
                                      InfoBarMessageType.Information);
        _ = WatchCheckoutAsync();
    }

    [RelayCommand]
    private void SeeWhatsIncluded() => Navigate(WinoPage.WinoIntelligencePage);

    /// <summary>Polls the entitlement while the page is open so it unlocks once Stripe confirms the purchase.</summary>
    private async Task WatchCheckoutAsync()
    {
        _checkoutWatch?.Cancel();
        var watch = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _checkoutWatch = watch;

        try
        {
            while (!watch.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), watch.Token).ConfigureAwait(false);
                var entitlement = await _entitlementService.RefreshEntitlementAsync(watch.Token).ConfigureAwait(false);
                if (entitlement.CanAccessSurfaces)
                {
                    await ExecuteUIThread(() => ApplyAccess(VoiceControlAccess.Available));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not refresh the Wino Intelligence entitlement after checkout.");
        }
        finally
        {
            // Not disposed: OnNavigatedFrom may cancel it from the UI thread at the same moment.
            if (ReferenceEquals(_checkoutWatch, watch))
                _checkoutWatch = null;
        }
    }

    [RelayCommand]
    private void TryVoiceControl()
    {
        if (IsAvailable && IsVoiceControlEnabled)
            Messenger.Send(new VoiceBubbleTryRequested());
    }

    private void Navigate(WinoPage page)
        => Messenger.Send(new BreadcrumbNavigationRequested(SettingsCatalog.Title(page), page));

    private async Task RefreshAccessAsync()
    {
        try
        {
            var entitlement = await _entitlementService.GetEntitlementAsync().ConfigureAwait(false);
            await ExecuteUIThread(() => ApplyAccess(ToAccess(entitlement)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the Wino Intelligence entitlement for voice control.");
            await ExecuteUIThread(() => ApplyAccess(Access));
        }
    }

    // Microphones and the voice mode are only read for Wino Intelligence users; a locked page touches neither.
    private void ApplyAccess(VoiceControlAccess access)
    {
        var wasUsable = _isWatchingMicrophones;
        Access = access;
        OnPropertyChanged(nameof(HasMicrophoneProblem));

        var isUsable = _isActive && IsAvailable;
        WatchMicrophones(isUsable);
        if (isUsable)
        {
            _ = LoadMicrophonesAsync();
            if (!wasUsable)
                _ = RefreshModeAsync();
        }
        else
        {
            IsTestingMicrophone = false;
        }
    }

    private void WatchMicrophones(bool watch)
    {
        if (watch == _isWatchingMicrophones)
            return;

        _isWatchingMicrophones = watch;
        if (watch)
            _microphoneService.DevicesChanged += MicrophoneDevicesChanged;
        else
            _microphoneService.DevicesChanged -= MicrophoneDevicesChanged;
    }

    private static VoiceControlAccess ToAccess(WinoIntelligenceEntitlementSnapshot? entitlement)
    {
        if (entitlement is null || entitlement.State == WinoIntelligenceEntitlementState.SignedOut)
            return VoiceControlAccess.SignedOut;

        return entitlement.CanAccessSurfaces ? VoiceControlAccess.Available : VoiceControlAccess.NotSubscribed;
    }

    private void MicrophoneDevicesChanged(object? sender, EventArgs e)
    {
        if (_isActive && IsAvailable)
            _ = ExecuteUIThread(() => _ = LoadMicrophonesAsync());
    }

    private async Task LoadMicrophonesAsync()
    {
        var devices = await _microphoneService.GetDevicesAsync().ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (!_isActive)
                return;

            var defaultDevice = devices.FirstOrDefault(device => device.IsDefault);
            var defaultName = defaultDevice is null
                ? Translator.VoiceControlSettings_Microphone_SystemDefault
                : string.Format(Translator.VoiceControlSettings_Microphone_SystemDefaultNamed, defaultDevice.Name);

            _isLoadingMicrophones = true;
            try
            {
                Microphones.Clear();
                Microphones.Add(new MicrophoneOption(string.Empty, defaultName));
                foreach (var device in devices)
                    Microphones.Add(new MicrophoneOption(device.Id, device.Name));

                // A saved microphone that is unplugged stays saved, so it is used again when it returns.
                var savedId = PreferencesService.VoiceMicrophoneDeviceId;
                SelectedMicrophone = Microphones.FirstOrDefault(option => string.Equals(option.Id, savedId, StringComparison.OrdinalIgnoreCase))
                    ?? Microphones[0];
            }
            finally
            {
                _isLoadingMicrophones = false;
            }

            MicrophoneAccess = devices.Count == 0 ? MicrophoneAccess.NoDevice : _microphoneService.GetAccessStatus();
            if (MicrophoneAccess != MicrophoneAccess.Allowed)
                IsTestingMicrophone = false;
        });

        if (IsTestingMicrophone)
            await RestartLevelMonitorAsync().ConfigureAwait(false);
    }

    private async Task RestartLevelMonitorAsync()
    {
        await _monitorGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopLevelMonitorCore();

            if (!_isActive || !IsAvailable || !IsVoiceControlEnabled || !IsTestingMicrophone)
            {
                await ExecuteUIThread(() => MicrophoneLevel = 0);
                return;
            }

            var cancellation = new CancellationTokenSource();
            _monitorCancellation = cancellation;

            var result = await _microphoneService
                .StartLevelMonitorAsync(PreferencesService.VoiceMicrophoneDeviceId, OnLevelChanged, cancellation.Token)
                .ConfigureAwait(false);

            if (cancellation.IsCancellationRequested || !_isActive || !IsTestingMicrophone)
            {
                result.Session?.Dispose();
                return;
            }

            _levelMonitor = result.Session;
            await ExecuteUIThread(() =>
            {
                MicrophoneAccess = result.Access;
                if (result.Access != MicrophoneAccess.Allowed)
                {
                    MicrophoneLevel = 0;
                    IsTestingMicrophone = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start the microphone level for voice control settings.");
            await ExecuteUIThread(() => MicrophoneAccess = MicrophoneAccess.Unavailable);
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private async Task StopLevelMonitorAsync()
    {
        _monitorCancellation?.Cancel();
        await _monitorGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopLevelMonitorCore();
        }
        finally
        {
            _monitorGate.Release();
        }

        await ExecuteUIThread(() => MicrophoneLevel = 0);
    }

    private void StopLevelMonitorCore()
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;

        var monitor = _levelMonitor;
        _levelMonitor = null;

        try
        {
            monitor?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not stop the microphone level monitor.");
        }
    }

    private void OnLevelChanged(double level)
    {
        if (_isActive && IsTestingMicrophone)
            _ = ExecuteUIThread(() => MicrophoneLevel = level);
    }
}
