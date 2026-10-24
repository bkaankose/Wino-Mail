#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// One row of the "App modes" settings group: whether the mode is offered by an account and
/// whether the user keeps it in the switcher.
/// </summary>
public sealed partial class AppModeOptionViewModel : ObservableObject
{
    private readonly Action<WinoApplicationMode, bool> _setHidden;
    private bool _isApplying;
    private bool _isAvailable;
    private bool _isShown;
    private bool _canToggle;
    private string _description = string.Empty;

    public AppModeOptionViewModel(WinoApplicationMode mode, Action<WinoApplicationMode, bool> setHidden)
    {
        Mode = mode;
        _setHidden = setHidden;
    }

    public WinoApplicationMode Mode { get; }

    public string Title => GetDisplayName(Mode);

    public WinoIconGlyph Icon => Mode switch
    {
        WinoApplicationMode.Calendar => WinoIconGlyph.Calendar,
        WinoApplicationMode.Contacts => WinoIconGlyph.People,
        WinoApplicationMode.Tasks => WinoIconGlyph.CheckmarkCircle,
        WinoApplicationMode.Settings => WinoIconGlyph.Settings,
        _ => WinoIconGlyph.Mail
    };

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public bool CanToggle
    {
        get => _canToggle;
        private set => SetProperty(ref _canToggle, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (!SetProperty(ref _isShown, value) || _isApplying)
                return;

            _setHidden(Mode, !value);
        }
    }

    public void Apply(AppModeVisibility visibility)
    {
        _isApplying = true;

        try
        {
            IsAvailable = visibility.IsAvailable(Mode);
            IsShown = visibility.IsVisible(Mode) && IsAvailable;
            CanToggle = IsAvailable && (!IsShown || visibility.CanHide(Mode));
            Description = !IsAvailable
                ? Translator.SettingsPersonalization_AppModes_UnavailableDescription
                : !IsShown
                    ? Translator.SettingsPersonalization_AppModes_HiddenDescription
                    : CanToggle
                        ? Translator.SettingsPersonalization_AppModes_ShownDescription
                        : Translator.SettingsPersonalization_AppModes_LastVisibleDescription;
        }
        finally
        {
            _isApplying = false;
        }
    }

    public static string GetDisplayName(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => Translator.KeyboardShortcuts_ModeCalendar,
        WinoApplicationMode.Contacts => Translator.ContactsPage_Title,
        WinoApplicationMode.Tasks => Translator.ToDoPage_Title,
        WinoApplicationMode.Settings => Translator.MenuSettings,
        _ => Translator.KeyboardShortcuts_ModeMail
    };
}
