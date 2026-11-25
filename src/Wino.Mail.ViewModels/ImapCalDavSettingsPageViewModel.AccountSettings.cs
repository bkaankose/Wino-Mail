#nullable enable
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The edit page as "Sign-in and servers" of the account settings: the apps are switched on their own pages, so this
/// page shows which services use the sign-in, hints for an unused DAV server, and the change an app page asked for.
/// </summary>
public partial class ImapCalDavSettingsPageViewModel
{
    private WinoApplicationMode? _requestedApp;

    public bool IsAccountSettingsEditMode => IsEditMode;

    /// <summary>"Used by incoming mail, outgoing mail and CalDAV" for the shared sign-in.</summary>
    public string SignInUsedByText
    {
        get
        {
            var users = new List<string>();

            if (IsMailSupportEnabled)
            {
                users.Add(Translator.AccountSignIn_UsedByIncoming);
                users.Add(Translator.AccountSignIn_UsedByOutgoing);
            }

            if (IsCalDavSettingsVisible && (!IsMailSupportEnabled || UseMailCredentialsForDav))
                users.Add(Translator.ProviderSelection_SourceCalDav);

            if (IsCardDavSettingsVisible && (!IsMailSupportEnabled || UseMailCredentialsForDav))
                users.Add(Translator.ProviderSelection_SourceCardDav);

            return users.Count == 0 ? string.Empty : string.Format(Translator.AccountSignIn_UsedBy, string.Join(", ", users));
        }
    }

    /// <summary>The POP3 download choices of an existing account are on its Mail page.</summary>
    public bool IsPop3ServerCopiesVisible => IsPop3 && IsCreateMode;

    /// <summary>An existing account keeps "Save a copy in Sent" on its Mail page.</summary>
    public bool IsAppendSentCopyOptionVisible => IsMailAdvancedOptionsVisible && IsCreateMode;

    public bool IsCalDavUnusedHintVisible => IsEditMode && !IsPop3 && !IsCalDavSettingsVisible;

    public bool IsCardDavUnusedHintVisible => IsEditMode && !IsPop3 && !IsCardDavSettingsVisible;

    public bool IsPendingAppMessageVisible => IsEditMode && _requestedApp != null;

    public string PendingAppMessage => _requestedApp is { } app
        ? string.Format(Translator.AccountSignIn_PendingAppMessage, AccountAppPresenter.GetTitle(app))
        : string.Empty;

    /// <summary>Selects the source an app page asked for, so its server row shows and Save applies it.</summary>
    private void ApplyRequestedAppMode(ImapCalDavSettingsNavigationContext context)
    {
        _requestedApp = null;

        if (!IsEditMode || context.RequestedApp is not { } app)
        {
            NotifyAccountSettingsLayoutChanged();
            return;
        }

        _requestedApp = app;

        switch (app)
        {
            case WinoApplicationMode.Mail:
                Capabilities.MailMode = context.RequestedMode;
                IsIncomingExpanded = true;
                IsOutgoingExpanded = true;
                break;
            case WinoApplicationMode.Calendar:
                Capabilities.CalendarMode = context.RequestedMode;
                IsCalDavExpanded = true;
                break;
            case WinoApplicationMode.Contacts:
                Capabilities.ContactMode = context.RequestedMode;
                IsCardDavExpanded = true;
                break;
            case WinoApplicationMode.Tasks:
                Capabilities.TaskMode = context.RequestedMode;
                IsCalDavExpanded = true;
                break;
        }

        NotifyAccountSettingsLayoutChanged();
    }

    private void NotifyAccountSettingsLayoutChanged()
    {
        OnPropertyChanged(nameof(IsAccountSettingsEditMode));
        OnPropertyChanged(nameof(IsPop3ServerCopiesVisible));
        OnPropertyChanged(nameof(IsAppendSentCopyOptionVisible));
        OnPropertyChanged(nameof(SignInUsedByText));
        OnPropertyChanged(nameof(IsCalDavUnusedHintVisible));
        OnPropertyChanged(nameof(IsCardDavUnusedHintVisible));
        OnPropertyChanged(nameof(IsPendingAppMessageVisible));
        OnPropertyChanged(nameof(PendingAppMessage));
    }

    partial void OnUseMailCredentialsForDavChanged(bool value) => OnPropertyChanged(nameof(SignInUsedByText));

    [RelayCommand]
    private void OpenCalendarApp() => OpenAccountApp(WinoApplicationMode.Calendar);

    [RelayCommand]
    private void OpenPeopleApp() => OpenAccountApp(WinoApplicationMode.Contacts);

    private void OpenAccountApp(WinoApplicationMode app)
    {
        if (_editingAccountId == System.Guid.Empty)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(AccountAppPresenter.GetTitle(app), AccountAppPresenter.GetPage(app), _editingAccountId));
    }
}
