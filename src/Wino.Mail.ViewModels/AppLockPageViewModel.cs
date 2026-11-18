#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Settings -> General -> App lock: picks how the app unlocks (the platform biometric prompt or the app's own PIN),
/// turns the lock on or off and changes the PIN. Every change while the lock is on asks the user to verify with the
/// current method first. Nothing here touches preferences or the database.
/// </summary>
public partial class AppLockPageViewModel : CoreBaseViewModel
{
    private readonly IAppLockService _appLockService;
    private readonly IAppLockPlatform _appLockPlatform;
    private readonly IMailDialogService _dialogService;

    private bool _isSyncingNotificationToggle;

    private bool _isSubscribed;

    public bool IsSupported => _appLockService.IsSupported;

    public bool IsNotSupported => !IsSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText), nameof(IsApplyVisible), nameof(CanTurnOff), nameof(IsLockOn))]
    public partial AppLockMethod? CurrentMethod { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHelloSelected), nameof(IsPinSelected), nameof(IsPinEntryVisible), nameof(ApplyText), nameof(IsApplyVisible))]
    public partial AppLockMethod SelectedMethod { get; set; } = AppLockMethod.Pin;

    public bool IsLockOn => CurrentMethod is not null;

    public bool IsHelloSelected => SelectedMethod == AppLockMethod.Biometric;

    public bool IsPinSelected => SelectedMethod == AppLockMethod.Pin;

    public bool IsPinEntryVisible => IsSupported && IsPinSelected;

    public AppLockMethodOption[] MethodOptions { get; }

    [ObservableProperty]
    public partial bool IsBiometricAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanTurnOff), nameof(CanPickMethod))]
    public partial bool IsBusy { get; set; }

    public bool CanPickMethod => IsSupported && !IsBusy;

    public bool CanApply => IsSupported && !IsBusy;

    public bool CanTurnOff => CurrentMethod is not null && !IsBusy;

    public string ApplyText => CurrentMethod switch
    {
        null => Translator.AppLock_TurnOn,
        AppLockMethod.Pin when IsPinSelected => Translator.AppLock_ChangePin,
        _ => IsHelloSelected ? Translator.AppLock_SwitchToHello : Translator.AppLock_SwitchToPin
    };

    // With the biometric method active and selected there is nothing left to apply.
    public bool IsApplyVisible => !(CurrentMethod == AppLockMethod.Biometric && IsHelloSelected);

    [ObservableProperty]
    public partial string NewPin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPin { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = string.Empty;

    public bool HasError => ErrorText.Length > 0;

    [ObservableProperty]
    public partial bool AreNotificationsReadOnly { get; set; }

    public AppLockPageViewModel(IMessenger messenger, IAppLockService appLockService, IMailDialogService dialogService, IAppLockPlatform appLockPlatform)
        : base(messenger)
    {
        _dialogService = dialogService;
        _appLockService = appLockService;
        _appLockPlatform = appLockPlatform;

        MethodOptions =
        [
            new(AppLockMethod.Biometric, Translator.AppLock_MethodHello, Translator.AppLock_MethodHelloDescription, WinoIconGlyph.WindowsHello),
            new(AppLockMethod.Pin, Translator.AppLock_MethodPin, Translator.AppLock_MethodPinDescription, WinoIconGlyph.Dialpad)
        ];

        SyncState(resetSelection: true);
    }

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (!_isSubscribed)
        {
            _appLockService.StateChanged += AppLockStateChanged;
            _isSubscribed = true;
        }

        SyncState(resetSelection: true);
        _ = LoadBiometricAvailabilityAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        if (_isSubscribed)
        {
            _appLockService.StateChanged -= AppLockStateChanged;
            _isSubscribed = false;
        }

        ClearPins();
        ClearMessages();
    }

    private void AppLockStateChanged(object? sender, EventArgs e) => _ = ExecuteUIThread(() => SyncState(resetSelection: false));

    private void SyncState(bool resetSelection)
    {
        var method = _appLockService.Method;
        var hasChanged = method != CurrentMethod;
        CurrentMethod = method;

        foreach (var option in MethodOptions)
            option.IsActive = option.Method == method;

        _isSyncingNotificationToggle = true;
        AreNotificationsReadOnly = _appLockService.AreNotificationsReadOnly;
        _isSyncingNotificationToggle = false;

        if (method is { } current && (resetSelection || hasChanged))
            SelectedMethod = current;
    }

    private async Task LoadBiometricAvailabilityAsync()
    {
        var isAvailable = await _appLockService.IsBiometricAvailableAsync();

        await ExecuteUIThread(() =>
        {
            IsBiometricAvailable = isAvailable;

            var hello = MethodOptions[0];
            hello.IsAvailable = isAvailable;
            hello.Description = isAvailable ? Translator.AppLock_MethodHelloDescription : Translator.AppLock_HelloUnavailable;

            // With the lock off, the method that needs nothing new to remember is offered first.
            if (CurrentMethod is null && isAvailable && NewPin.Length == 0)
                SelectedMethod = AppLockMethod.Biometric;
        });
    }

    /// <summary>Picks the method the next apply uses. Returns false when the method cannot be picked now.</summary>
    public bool SelectMethod(AppLockMethod method)
    {
        if (!CanPickMethod || (method == AppLockMethod.Biometric && !IsBiometricAvailable)) return false;
        if (SelectedMethod == method) return true;

        ClearMessages();
        SelectedMethod = method;
        return true;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply) return;

        ClearMessages();

        if (IsPinSelected)
        {
            if (!AppLockPinRules.IsValid(NewPin))
            {
                ErrorText = Translator.AppLock_PinInvalid;
                return;
            }

            if (!string.Equals(NewPin, ConfirmPin, StringComparison.Ordinal))
            {
                ErrorText = Translator.AppLock_PinMismatch;
                return;
            }
        }

        IsBusy = true;

        try
        {
            var wasPin = CurrentMethod == AppLockMethod.Pin;
            var wasOn = CurrentMethod is not null;

            if (CurrentMethod is not null && !await _appLockService.RequestVerificationAsync()) return;

            if (IsHelloSelected)
            {
                // Proves the prompt works for this user before it becomes the only way in.
                if (!await _appLockPlatform.VerifyBiometricAsync())
                {
                    ErrorText = Translator.AppLock_HelloFailed;
                    return;
                }

                if (_appLockService.EnableWithBiometric())
                    ShowSuccess(Translator.AppLock_TurnedOnHelloMessage);
                else
                    ErrorText = Translator.AppLock_StorageFailed;

                return;
            }

            if (_appLockService.EnableWithPin(NewPin))
                ShowSuccess(wasOn && wasPin ? Translator.AppLock_PinChangedMessage : Translator.AppLock_TurnedOnPinMessage);
            else
                ErrorText = Translator.AppLock_StorageFailed;
        }
        finally
        {
            ClearPins();
            SyncState(resetSelection: false);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TurnOffAsync()
    {
        if (!CanTurnOff) return;

        ClearMessages();
        IsBusy = true;

        try
        {
            if (!await _appLockService.RequestVerificationAsync()) return;

            _appLockService.Disable();

            if (_appLockService.IsEnabled)
                ErrorText = Translator.AppLock_StorageFailed;
            else
                ShowSuccess(Translator.AppLock_TurnedOffMessage);
        }
        finally
        {
            ClearPins();
            SyncState(resetSelection: false);
            IsBusy = false;
        }
    }

    partial void OnAreNotificationsReadOnlyChanged(bool value)
    {
        if (_isSyncingNotificationToggle) return;

        _ = ApplyNotificationsReadOnlyAsync(value);
    }

    private async Task ApplyNotificationsReadOnlyAsync(bool isReadOnly)
    {
        ClearMessages();

        // Bringing the action buttons back weakens the lock, so only that direction asks the user to verify.
        var isApplied = (isReadOnly || await _appLockService.RequestVerificationAsync())
            && _appLockService.SetNotificationsReadOnly(isReadOnly);

        if (isApplied) return;

        if (_appLockService.IsEnabled && _appLockService.AreNotificationsReadOnly != isReadOnly && isReadOnly)
            ErrorText = Translator.AppLock_StorageFailed;

        // Yield so the toggle's own two-way update finishes before it is set back.
        await Task.Yield();
        SyncState(resetSelection: false);
    }

    private void ShowSuccess(string message)
        => _dialogService.InfoBarMessage(Translator.AppLock_Title, message, InfoBarMessageType.Success);

    private void ClearPins()
    {
        NewPin = string.Empty;
        ConfirmPin = string.Empty;
    }

    private void ClearMessages()
    {
        ErrorText = string.Empty;
    }
}
