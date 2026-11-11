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
using Serilog;
using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Services.Assistant;

namespace Wino.Core.ViewModels;

/// <summary>
/// Settings › Voice Control › What Wino remembers: the brain on this device (stated facts, answers to questions and
/// accepted proposals), grouped, each with a forget button, and a way to forget everything. Nothing here comes from
/// the server: the brain lives on the device and, when brain sync is on, travels encrypted between the user's devices.
/// </summary>
public partial class VoiceBrainPageViewModel : CoreBaseViewModel
{
    private readonly IAgentRuntimeHost _runtime;
    private readonly IAccountService _accountService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoAccountProfileService _profile;
    private CancellationTokenSource? _loadCancellation;

    public VoiceBrainPageViewModel(IAgentRuntimeHost runtime, IAccountService accountService, IMailDialogService dialogService, IWinoAccountProfileService profile)
    {
        _runtime = runtime;
        _accountService = accountService;
        _dialogService = dialogService;
        _profile = profile;
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
    [NotifyCanExecuteChangedFor(nameof(DeleteEverythingCommand))]
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
        _runtime.Brain.Changed += BrainChanged;
        _runtime.Sync.StatusChanged += SyncStatusChanged;
        _ = LoadAsync();
        _ = RefreshSyncAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        _runtime.Brain.Changed -= BrainChanged;
        _runtime.Sync.StatusChanged -= SyncStatusChanged;
        _loadCancellation?.Cancel();
    }

    // Brain sync: the memory travels between the user's devices encrypted with a password the server never sees.

    /// <summary>A Wino Account is signed in, so the memory can be synced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncDescription))]
    [NotifyCanExecuteChangedFor(nameof(TurnOnSyncCommand))]
    public partial bool CanSync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncOff), nameof(SyncDescription))]
    [NotifyCanExecuteChangedFor(nameof(TurnOnSyncCommand), nameof(TurnOffSyncCommand), nameof(ChangeSyncPasswordCommand))]
    public partial bool IsSyncOn { get; set; }

    public bool IsSyncOff => !IsSyncOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncDescription))]
    public partial string SyncStatusText { get; set; } = string.Empty;

    /// <summary>Off: what sync is, with the sign-in hint when it cannot be turned on. On: where it stands.</summary>
    public string SyncDescription => IsSyncOn
        ? SyncStatusText
        : CanSync ? Translator.VoiceBrain_Sync_Description : Translator.VoiceBrain_Sync_Description + Environment.NewLine + SyncStatusText;

    /// <summary>The server copy was made with another password: the user enters it, or replaces the copy.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnterSyncPasswordCommand), nameof(ReplaceServerCopyCommand))]
    public partial bool IsSyncPasswordMismatch { get; set; }

    [ObservableProperty]
    public partial bool IsSyncFailed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TurnOnSyncCommand), nameof(TurnOffSyncCommand), nameof(ChangeSyncPasswordCommand), nameof(EnterSyncPasswordCommand), nameof(ReplaceServerCopyCommand))]
    public partial bool IsSyncBusy { get; set; }

    private void SyncStatusChanged(object? sender, EventArgs e) => _ = ExecuteUIThread(ApplySyncStatus);

    private async Task RefreshSyncAsync()
    {
        bool signedIn;
        try
        {
            await _runtime.EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
            signedIn = await _profile.HasActiveAccountAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the brain sync state.");
            signedIn = false;
        }

        await ExecuteUIThread(() =>
        {
            CanSync = signedIn;
            ApplySyncStatus();
        });
    }

    private void ApplySyncStatus()
    {
        var status = _runtime.Sync.Status;
        IsSyncOn = status.IsEnabled;
        IsSyncPasswordMismatch = status.IsEnabled && status.Problem == BrainSyncProblem.WrongPassword;
        SyncStatusText = !CanSync && !status.IsEnabled
            ? Translator.VoiceBrain_Sync_NeedsAccount
            : !status.IsEnabled
                ? Translator.VoiceBrain_Sync_Off
                : status.Problem switch
                {
                    BrainSyncProblem.WrongPassword => Translator.VoiceBrain_Sync_WrongPassword,
                    BrainSyncProblem.Offline => Translator.VoiceBrain_Sync_Offline,
                    BrainSyncProblem.InvalidServerCopy => Translator.VoiceBrain_Sync_InvalidServerCopy,
                    BrainSyncProblem.TooLarge => Translator.VoiceBrain_Sync_TooLarge,
                    _ when status.LastSyncedUtc is { } synced => string.Format(CultureInfo.CurrentCulture, Translator.VoiceBrain_Sync_OnSince, synced.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                    _ => Translator.VoiceBrain_Sync_On,
                };
    }

    private bool CanTurnOnSync() => CanSync && !IsSyncOn && !IsSyncBusy;
    private bool CanChangeSync() => IsSyncOn && !IsSyncBusy;
    private bool CanAnswerMismatch() => IsSyncPasswordMismatch && !IsSyncBusy;

    [RelayCommand(CanExecute = nameof(CanTurnOnSync))]
    private async Task TurnOnSyncAsync()
    {
        var password = await AskSyncPasswordAsync(isNew: true, wasRejected: false).ConfigureAwait(false);
        if (password is null)
            return;

        await RunSyncAsync(ct => _runtime.Sync.EnableAsync(password, ct)).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSync))]
    private async Task ChangeSyncPasswordAsync()
    {
        var password = await AskSyncPasswordAsync(isNew: true, wasRejected: false).ConfigureAwait(false);
        if (password is null)
            return;

        await RunSyncAsync(ct => _runtime.Sync.ChangePasswordAsync(password, ct)).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSync))]
    private async Task TurnOffSyncAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.VoiceBrain_Sync_TurnOff_DialogMessage,
            Translator.VoiceBrain_Sync_TurnOff_DialogTitle,
            Translator.VoiceBrain_Sync_TurnOff_Button).ConfigureAwait(false);
        if (!confirmed)
            return;

        await RunSyncAsync(ct => _runtime.Sync.DisableAsync(deleteServerCopy: true, ct)).ConfigureAwait(false);
    }

    /// <summary>Asks for the password of the server copy until it opens or the user gives up.</summary>
    [RelayCommand(CanExecute = nameof(CanAnswerMismatch))]
    private async Task EnterSyncPasswordAsync()
    {
        var rejected = false;
        while (true)
        {
            var password = await AskSyncPasswordAsync(isNew: false, wasRejected: rejected).ConfigureAwait(false);
            if (password is null)
                return;

            await RunSyncAsync(ct => _runtime.Sync.EnterPasswordAsync(password, ct)).ConfigureAwait(false);
            if (_runtime.Sync.Status.Problem != BrainSyncProblem.WrongPassword || IsSyncFailed)
                return;

            rejected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnswerMismatch))]
    private async Task ReplaceServerCopyAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.VoiceBrain_Sync_Replace_DialogMessage,
            Translator.VoiceBrain_Sync_Replace_DialogTitle,
            Translator.VoiceBrain_Sync_Replace_Button).ConfigureAwait(false);
        if (!confirmed)
            return;

        await RunSyncAsync(ct => _runtime.Sync.ReplaceServerCopyAsync(ct)).ConfigureAwait(false);
    }

    /// <summary>The password goes from the dialog to the sync and nowhere else: never logged or kept here.</summary>
    private Task<string?> AskSyncPasswordAsync(bool isNew, bool wasRejected)
        => _dialogService.ShowWinoAccountSyncSecretDialogAsync(new SyncSnapshotSecretRequest(
            IsPassphrase: true,
            WasRejected: wasRejected,
            IsNewBackup: isNew,
            Title: isNew ? Translator.VoiceBrain_Sync_NewPassword_Title : Translator.VoiceBrain_Sync_Password_Title,
            Description: isNew ? Translator.VoiceBrain_Sync_NewPassword_Description : Translator.VoiceBrain_Sync_Password_Description,
            FieldLabel: Translator.VoiceBrain_Sync_PasswordLabel,
            ConfirmLabel: Translator.VoiceBrain_Sync_ConfirmPasswordLabel));

    private async Task RunSyncAsync(Func<CancellationToken, Task> work)
    {
        await ExecuteUIThread(() =>
        {
            IsSyncBusy = true;
            IsSyncFailed = false;
        });
        try
        {
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Brain sync action failed.");
            await ExecuteUIThread(() => IsSyncFailed = true);
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                IsSyncBusy = false;
                ApplySyncStatus();
            });
        }
    }

    private void BrainChanged(object? sender, EventArgs e) => _ = LoadAsync();

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
            await _runtime.Brain.ForgetAllAsync(CancellationToken.None).ConfigureAwait(false);
            await ExecuteUIThread(() => Apply([], []));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not forget everything Wino remembers.");
            await ExecuteUIThread(() => IsActionFailed = true);
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

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
            await _runtime.EnsureInitializedAsync(cancellation.Token).ConfigureAwait(false);
            var entries = _runtime.Brain.Entries.Where(static e => e.DeletedAt is null && e.Kind != BrainEntryKind.Declined).ToList();
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
                IsOffline = false;
                Apply(entries, accounts);
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

    private void Apply(List<BrainEntry> entries, List<MailAccount> accounts)
    {
        People.Clear();
        Accounts.Clear();
        Folders.Clear();
        Defaults.Clear();
        Sections.Clear();

        foreach (var entry in entries.OrderBy(static e => e.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = ToItem(entry, accounts);
            switch (entry.Kind)
            {
                case BrainEntryKind.PersonRelation or BrainEntryKind.PersonAlias or BrainEntryKind.SenderLabel or BrainEntryKind.Group:
                    People.Add(item);
                    break;
                case BrainEntryKind.AccountNickname:
                    Accounts.Add(item);
                    break;
                case BrainEntryKind.ContainerSynonym:
                    Folders.Add(item);
                    break;
                default:
                    Defaults.Add(item);
                    break;
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

    private static string Capitalized(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..];

    /// <summary>One line per entry: what the user said (or the concept) and what it points at on this device.</summary>
    private VoiceBrainFactItem ToItem(BrainEntry entry, List<MailAccount> accounts)
    {
        var subject = entry.Subject;
        // A default is stored about the user ("self"); what it points at is the device key in its value.
        var separator = entry.Kind == BrainEntryKind.Default ? entry.Value.IndexOf('=') : -1;
        var defaultTarget = separator > 0 ? ItemRef.Parse(entry.Value[(separator + 1)..]) : null;
        var accountId = subject.AccountId ?? defaultTarget?.AccountId;
        var account = accountId is { } id ? accounts.FirstOrDefault(a => a.Id == id) : null;
        var accountTitle = account is null ? null
            : !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address;
        var target = subject.Name ?? subject.Address ?? subject.Text;
        // What the user called it ("my boss") helps; the whole sentence that stated the fact, which names the target, does not.
        var said = entry.SaidAs.FirstOrDefault() is { } words && !(target is not null && words.Contains(target, StringComparison.OrdinalIgnoreCase)) ? words : null;

        var (title, detail) = entry.Kind switch
        {
            BrainEntryKind.Default when entry.Value.StartsWith("calendar=", StringComparison.Ordinal) => (Translator.VoiceBrain_DefaultCalendar,
                defaultTarget?.Kind == ItemKind.Account ? accountTitle ?? target : Join(target ?? said, accountTitle)),
            BrainEntryKind.Default when entry.Value.StartsWith("task", StringComparison.Ordinal) => (Translator.VoiceBrain_DefaultTaskAccount,
                defaultTarget?.Kind == ItemKind.Account ? accountTitle ?? target : Join(target ?? said, accountTitle)),
            BrainEntryKind.Default => (Translator.VoiceBrain_DefaultMailAccount, accountTitle ?? target),
            BrainEntryKind.AccountNickname => (said ?? entry.Value, accountTitle ?? target),
            BrainEntryKind.Pick => (said ?? subject.Text ?? entry.Value, Join(target, accountTitle)),
            _ => (said is null || string.Equals(said, entry.Value, StringComparison.OrdinalIgnoreCase) ? Capitalized(entry.Value) : $"{Capitalized(entry.Value)} ({said})", Join(target, accountTitle)),
        };

        return new VoiceBrainFactItem(
            entry.Id,
            title,
            detail,
            string.Format(CultureInfo.CurrentCulture, Translator.VoiceBrain_DeleteFact, title),
            DeleteFactAsync);
    }

    private async Task DeleteFactAsync(VoiceBrainFactItem item)
    {
        item.IsDeleting = true;
        IsActionFailed = false;
        try
        {
            await _runtime.Brain.ForgetAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not forget a brain entry.");
            await ExecuteUIThread(() =>
            {
                item.IsDeleting = false;
                IsActionFailed = true;
            });
            return;
        }

        await ExecuteUIThread(() =>
        {
            item.IsDeleting = false;
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

    private static string? Join(string? first, string? second)
    {
        var parts = new[] { first, second }.Where(static part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }
}
