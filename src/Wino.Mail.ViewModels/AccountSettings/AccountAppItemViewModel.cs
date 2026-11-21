#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>
/// One app of an account with its switch: a row on the account page, and the switch at the top of the app's page.
/// Flipping <see cref="IsOn"/> asks the owner to change the app; the owner calls <see cref="Apply"/> with the result,
/// which puts the switch back when the change was cancelled or refused.
/// </summary>
public sealed partial class AccountAppItemViewModel : ObservableObject
{
    private readonly Action<AccountAppItemViewModel, bool> _toggleRequested;
    private bool _isApplying;

    public AccountAppItemViewModel(WinoApplicationMode app, Action<AccountAppItemViewModel, bool> toggleRequested)
    {
        App = app;
        _toggleRequested = toggleRequested;
    }

    public WinoApplicationMode App { get; }

    public string Title => AccountAppPresenter.GetTitle(App);

    public WinoIconGlyph Icon => AccountAppPresenter.GetIcon(App);

    public string UseTitle => string.Format(Translator.AccountApps_UseAppTitle, Title);

    public string UseDescription => AccountAppPresenter.GetUseDescription(App);

    public string OpenAutomationName => string.Format(Translator.AccountApps_OpenAppSettings, Title);

    public AccountAppState? State { get; private set; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsOffered { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsLastApp { get; set; }

    [ObservableProperty]
    public partial bool NeedsSignIn { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool CanToggle => !IsBusy && !IsLastApp;

    public string ToggleHint => IsLastApp ? Translator.AccountApps_LastAppHint : string.Empty;

    public void Apply(MailAccount account, AccountAppState state, bool isLastApp)
    {
        _isApplying = true;

        try
        {
            State = state;
            IsOffered = state.IsOffered;
            IsOn = state.IsOn;
            IsLastApp = isLastApp;
            NeedsSignIn = state.NeedsSignIn;
            Summary = AccountAppPresenter.GetSummary(account, state);
            OnPropertyChanged(nameof(ToggleHint));
        }
        finally
        {
            _isApplying = false;
        }
    }

    /// <summary>Puts the switch back to the stored state after a cancelled or failed change.</summary>
    public void Revert()
    {
        _isApplying = true;

        try
        {
            IsOn = State?.IsOn == true;
        }
        finally
        {
            _isApplying = false;
        }
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_isApplying || State == null || State.IsOn == value)
            return;

        _toggleRequested(this, value);
    }
}

/// <summary>One choice of the "Syncs with" group on an app's page.</summary>
public sealed class AccountAppSourceOption(AccountCapabilityMode mode, string title, string description)
{
    public AccountCapabilityMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    /// <summary>The list control names each choice after its item, so screen readers read the title.</summary>
    public override string ToString() => Title;
}
