using MailCal.Core.Domain.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Messaging.Client.Shell;

namespace MailCal.Core.ViewModels;

/// <summary>
/// Settings › Companion. The enable switch reads and writes the preference directly. The global
/// shortcut stays in the Windows page because it registers a system hotkey there.
/// </summary>
public partial class CompanionSettingsPageViewModel(IPreferencesService preferencesService) : CoreBaseViewModel,
    IRecipient<PreferenceChangedMessage>
{
    public IPreferencesService PreferencesService { get; } = preferencesService;

    public bool IsCompanionEnabled
    {
        get => PreferencesService.IsCompanionEnabled;
        set
        {
            if (PreferencesService.IsCompanionEnabled == value) return;
            PreferencesService.IsCompanionEnabled = value;
            OnPropertyChanged();
        }
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<PreferenceChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<PreferenceChangedMessage>(this);
    }

    /// <summary>Follows changes made elsewhere (another window, the Windows page's own bindings).</summary>
    public void Receive(PreferenceChangedMessage message)
    {
        if (message.PropertyName == nameof(IPreferencesService.IsCompanionEnabled))
            _ = ExecuteUIThread(() => OnPropertyChanged(nameof(IsCompanionEnabled)));
    }
}
