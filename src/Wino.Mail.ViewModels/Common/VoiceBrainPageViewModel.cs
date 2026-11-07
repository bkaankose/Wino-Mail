#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

/// <summary>
/// Settings › Voice Control › What Wino remembers: the learned facts, grouped, each with a delete button, the mailboxes
/// the server keeps a section for, and the ways to forget everything. The brain is downloaded on each visit; offline it
/// shows the copy cached at the last download.
/// </summary>
public partial class VoiceBrainPageViewModel : CoreBaseViewModel, IRecipient<VoiceModeChanged>
{
    private readonly IVoiceBrainService _voiceBrain;
    private readonly IAccountService _accountService;
    private readonly IMailDialogService _dialogService;
    private CancellationTokenSource? _loadCancellation;

    public VoiceBrainPageViewModel(IVoiceBrainService voiceBrain, IAccountService accountService, IMailDialogService dialogService)
    {
        _voiceBrain = voiceBrain;
        _accountService = accountService;
        _dialogService = dialogService;
    }

    public ObservableCollection<VoiceBrainFactItem> People { get; } = [];
    public ObservableCollection<VoiceBrainFactItem> Accounts { get; } = [];
    public ObservableCollection<VoiceBrainFactItem> Folders { get; } = [];
    public ObservableCollection<VoiceBrainFactItem> Defaults { get; } = [];
    public ObservableCollection<VoiceBrainSectionItem> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasContent))]
    public partial bool IsLoading { get; set; }

    /// <summary>The server could not be reached and the page shows the cached copy, or nothing.</summary>
    [ObservableProperty]
    public partial bool IsOffline { get; set; }

    [ObservableProperty]
    public partial bool IsActionFailed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasContent))]
    [NotifyCanExecuteChangedFor(nameof(DeleteEverythingCommand))]
    public partial bool HasLoaded { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteEverythingCommand), nameof(TurnOffPersonalModeCommand))]
    public partial bool IsBusy { get; set; }

    public bool HasPeople => People.Count > 0;
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasFolders => Folders.Count > 0;
    public bool HasDefaults => Defaults.Count > 0;
    public bool HasSections => Sections.Count > 0;
    public bool HasFacts => HasPeople || HasAccounts || HasFolders || HasDefaults;
    public bool HasContent => !IsLoading && HasLoaded && (HasFacts || HasSections);
    public bool IsEmpty => !IsLoading && HasLoaded && !HasFacts && !HasSections;

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _ = LoadAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        _loadCancellation?.Cancel();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<VoiceModeChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();
        Messenger.Unregister<VoiceModeChanged>(this);
    }

    public void Receive(VoiceModeChanged message)
    {
        if (message.Mode == VoiceMode.Private)
            _ = ExecuteUIThread(LeavePage);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private bool CanChangeEverything() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanChangeEverything))]
    private async Task DeleteEverythingAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.VoiceBrain_DeleteAll_DialogMessage,
            Translator.VoiceBrain_DeleteAll_DialogTitle,
            Translator.VoiceBrain_DeleteAll_DialogConfirm);
        if (!confirmed)
            return;

        IsBusy = true;
        IsActionFailed = false;
        try
        {
            var deleted = await _voiceBrain.DeleteBrainAsync().ConfigureAwait(false);
            if (deleted)
            {
                await ExecuteUIThread(() => Apply(null));
                return;
            }

            await ExecuteUIThread(() => IsActionFailed = true);
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeEverything))]
    private async Task TurnOffPersonalModeAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.VoiceBrain_TurnOff_DialogMessage,
            Translator.VoiceBrain_TurnOff_DialogTitle,
            Translator.VoiceBrain_TurnOff_DialogConfirm);
        if (!confirmed)
            return;

        IsBusy = true;
        IsActionFailed = false;
        try
        {
            var changed = await _voiceBrain.SetModeAsync(VoiceMode.Private).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                if (changed)
                    LeavePage();
                else
                    IsActionFailed = true;
            });
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

    private void LeavePage()
        => Messenger.Send(new BreadcrumbNavigationRequested(SettingsCatalog.Title(WinoPage.VoiceControlSettingsPage), WinoPage.VoiceControlSettingsPage));

    private async Task LoadAsync()
    {
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;

        await ExecuteUIThread(() =>
        {
            IsLoading = true;
            IsActionFailed = false;
        });

        try
        {
            var snapshot = await _voiceBrain.GetBrainAsync(cancellation.Token).ConfigureAwait(false);
            List<MailAccount> accounts;
            try
            {
                accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load the accounts for What Wino remembers.");
                accounts = [];
            }

            if (cancellation.IsCancellationRequested)
                return;

            await ExecuteUIThread(() =>
            {
                IsOffline = snapshot is null or { IsCached: true };
                Apply(snapshot?.Brain, accounts);
                HasLoaded = true;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load What Wino remembers.");
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                await ExecuteUIThread(() => IsLoading = false);
        }
    }

    private void Apply(VoiceBrainDto? brain, List<MailAccount>? accounts = null)
    {
        People.Clear();
        Accounts.Clear();
        Folders.Clear();
        Defaults.Clear();
        Sections.Clear();

        if (brain is not null)
        {
            var local = accounts ?? [];
            foreach (var fact in (brain.Facts ?? []).OrderBy(static fact => fact.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = ToItem(fact, local);
                switch (fact.Kind)
                {
                    case BrainFactKind.Person:
                        People.Add(item);
                        break;
                    case BrainFactKind.AccountNickname:
                        Accounts.Add(item);
                        break;
                    case BrainFactKind.FolderSynonym:
                        Folders.Add(item);
                        break;
                    case BrainFactKind.DefaultCalendar:
                        Defaults.Add(item);
                        break;
                }
            }

            foreach (var stored in (brain.Sections ?? []).OrderBy(static stored => stored.Section.Mailbox.Address, StringComparer.OrdinalIgnoreCase))
            {
                var mailbox = stored.Section.Mailbox;
                var title = MailboxTitle(mailbox, local);
                var updated = string.Format(CultureInfo.CurrentCulture, Translator.VoiceBrain_SectionUpdated, stored.UpdatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                var detail = string.Equals(title, mailbox.Address, StringComparison.OrdinalIgnoreCase) ? updated : $"{mailbox.Address} · {updated}";
                Sections.Add(new VoiceBrainSectionItem(title, detail));
            }
        }

        OnPropertyChanged(nameof(HasPeople));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(HasDefaults));
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(HasFacts));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private VoiceBrainFactItem ToItem(BrainFact fact, List<MailAccount> accounts)
    {
        var target = fact.TargetName?.Trim();
        var mailbox = fact.Mailbox is { } key ? MailboxTitle(key, accounts) : null;

        var (title, detail) = fact.Kind switch
        {
            BrainFactKind.Person => (fact.Label, target),
            BrainFactKind.AccountNickname => (fact.Label, mailbox ?? target),
            BrainFactKind.DefaultCalendar => (Translator.VoiceBrain_DefaultCalendar, Join(target, mailbox)),
            _ => (fact.Label, Join(target, mailbox))
        };

        return new VoiceBrainFactItem(
            fact.Id,
            title,
            detail,
            string.Format(CultureInfo.CurrentCulture, Translator.VoiceBrain_DeleteFact, title),
            DeleteFactAsync);
    }

    private async Task DeleteFactAsync(VoiceBrainFactItem item)
    {
        item.IsDeleting = true;
        IsActionFailed = false;

        var deleted = await _voiceBrain.DeleteFactAsync(item.Id).ConfigureAwait(false);
        await ExecuteUIThread(() =>
        {
            item.IsDeleting = false;
            if (!deleted)
            {
                IsActionFailed = true;
                return;
            }

            People.Remove(item);
            Accounts.Remove(item);
            Folders.Remove(item);
            Defaults.Remove(item);
            OnPropertyChanged(nameof(HasPeople));
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(HasFolders));
            OnPropertyChanged(nameof(HasDefaults));
            OnPropertyChanged(nameof(HasFacts));
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    private static string MailboxTitle(BrainMailboxKey mailbox, List<MailAccount> accounts)
    {
        var account = accounts.FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.Address) && mailbox.Matches(account.Address, VoiceProviderKinds.ToProviderKind(account.ProviderType)));
        if (account is null)
            return mailbox.Address;

        return !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : mailbox.Address;
    }

    private static string? Join(string? first, string? second)
    {
        var parts = new[] { first, second }.Where(static part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }
}
