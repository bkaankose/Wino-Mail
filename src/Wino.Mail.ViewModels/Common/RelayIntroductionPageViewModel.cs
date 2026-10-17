#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

/// <summary>
/// The one-time introduction existing desktop users see after the update that brings the relay: two cards, timer-based
/// or relay-based sync. The page shows the privacy policy itself (a platform dialog) before <see cref="CompleteWithRelayAsync"/>.
/// </summary>
public partial class RelayIntroductionPageViewModel : CoreBaseViewModel
{
    private readonly IRelayIntroductionService _introduction;
    private readonly IPreferencesService _preferences;

    public RelayIntroductionPageViewModel(IRelayIntroductionService introduction, IPreferencesService preferences)
    {
        _introduction = introduction;
        _preferences = preferences;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimerSelected))]
    [NotifyPropertyChangedFor(nameof(IsRelaySelected))]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    public partial SyncMethod? SelectedMethod { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsTimerSelected => SelectedMethod == SyncMethod.Timer;

    public bool IsRelaySelected => SelectedMethod == SyncMethod.Relay;

    public bool CanContinue => SelectedMethod is not null && !IsBusy;

    /// <summary>The timer card quotes the user's current mail interval.</summary>
    public string TimerDescription => string.Format(Translator.RelayIntroduction_TimerDescriptionFormat, _preferences.EmailSyncIntervalMinutes);

    /// <summary>The policy must be shown before relay-based sync is enabled on this install.</summary>
    public bool IsPolicyAcceptanceRequired => _introduction.IsPolicyAcceptanceRequired;

    public void SelectTimer() => SelectedMethod = SyncMethod.Timer;

    public void SelectRelay() => SelectedMethod = SyncMethod.Relay;

    public void ClearSelection() => SelectedMethod = null;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanContinue));

    public void CompleteWithTimer() => _introduction.CompleteWithTimer();

    /// <summary>Records the policy acceptance and switches the app to relay-based sync. Returns false when the switch failed.</summary>
    public async Task<bool> CompleteWithRelayAsync()
    {
        IsBusy = true;
        try
        {
            await _introduction.CompleteWithRelayAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Switching to relay-based sync from the introduction failed.");
            return false;
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }
}
