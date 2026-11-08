#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Wino.Core.Domain.Models.Reader;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Settings -> Privacy &amp; security -> Sender rules: the per-address and per-domain exceptions to the remote content mode.
/// </summary>
public partial class RemoteContentRulesPageViewModel : CoreBaseViewModel,
    IRecipient<RemoteContentRulesChangedMessage>
{
    private readonly IRemoteContentRuleStore _ruleStore;
    private readonly IContactService _contactService;
    private readonly IMailDialogService _dialogService;

    private readonly List<RemoteContentRuleViewModel> _allRules = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public RemoteContentPolicyOption[] PolicyOptions { get; }

    /// <summary>"All policies" followed by <see cref="PolicyOptions"/>.</summary>
    public RemoteContentPolicyOption[] PolicyFilterOptions { get; }

    /// <summary>The rules that pass <see cref="Filter"/> and <see cref="PolicyFilter"/>, newest first.</summary>
    public ObservableCollection<RemoteContentRuleViewModel> Rules { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    public partial string NewPattern { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewPolicy))]
    public partial RemoteContentPolicyOption? NewPolicyOption { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyFilter))]
    public partial RemoteContentPolicyOption? SelectedPolicyFilterOption { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    public RemoteContentSenderPolicy NewPolicy
    {
        get => NewPolicyOption?.Policy ?? RemoteContentSenderPolicy.LoadEverything;
        set => NewPolicyOption = PolicyOptions.First(option => option.Policy == value);
    }

    public RemoteContentSenderPolicy? PolicyFilter
    {
        get => SelectedPolicyFilterOption?.Policy;
        set => SelectedPolicyFilterOption = PolicyFilterOptions.First(option => option.Policy == value);
    }

    public int TotalCount => _allRules.Count;
    public int VisibleCount => Rules.Count;
    public bool HasRules => TotalCount > 0;
    public bool IsEmpty => !IsLoading && TotalCount == 0;
    public bool HasNoMatches => TotalCount > 0 && VisibleCount == 0;
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Filter) || PolicyFilter != null;

    /// <summary>True when a filter hides some rules, so the footer shows "Showing x of y" and Show all.</summary>
    public bool IsShowingSubset => IsFiltered && HasRules;

    public string CountText => TotalCount switch
    {
        0 => Translator.PrivacySecurity_SenderRules_CountNone,
        1 => Translator.PrivacySecurity_SenderRules_CountOne,
        _ => string.Format(Translator.PrivacySecurity_SenderRules_Count, TotalCount)
    };

    public string ShowingText => string.Format(Translator.RemoteContentRules_ShowingCount, VisibleCount, TotalCount);

    public RemoteContentRulesPageViewModel(IRemoteContentRuleStore ruleStore,
                                           IContactService contactService,
                                           IMailDialogService dialogService)
    {
        _ruleStore = ruleStore;
        _contactService = contactService;
        _dialogService = dialogService;

        PolicyOptions = RemoteContentPolicyOption.CreatePolicies();
        PolicyFilterOptions = RemoteContentPolicyOption.CreateFilters(PolicyOptions);
        NewPolicyOption = PolicyOptions[0];
        SelectedPolicyFilterOption = PolicyFilterOptions[0];
    }

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        _ = ReloadAsync();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<RemoteContentRulesChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<RemoteContentRulesChangedMessage>(this);
    }

    public void Receive(RemoteContentRulesChangedMessage message) => _ = RefreshPatternAsync(message.Pattern);

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedPolicyFilterOptionChanged(RemoteContentPolicyOption? value) => ApplyFilter();

    private bool CanAddRule() => !string.IsNullOrWhiteSpace(NewPattern);

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private async Task AddRuleAsync()
    {
        var pattern = RemoteContentRulePattern.Normalize(NewPattern, out var kind);

        if (pattern == null)
        {
            _dialogService.InfoBarMessage(Translator.RemoteContentRules_AddFailedTitle, Translator.RemoteContentRules_InvalidPattern, InfoBarMessageType.Warning);
            return;
        }

        try
        {
            if (await _ruleStore.GetByPatternAsync(pattern) != null)
            {
                _dialogService.InfoBarMessage(Translator.RemoteContentRules_AddFailedTitle,
                                              string.Format(Translator.RemoteContentRules_AlreadyExists, pattern),
                                              InfoBarMessageType.Warning);
                return;
            }

            await _ruleStore.UpsertAsync(new RemoteContentSenderRule
            {
                Pattern = pattern,
                Kind = kind,
                Policy = NewPolicy,
                Source = RemoteContentRuleSource.Manual,
                CreatedUtc = DateTime.UtcNow
            });

            await ExecuteUIThread(() => NewPattern = string.Empty);
        }
        catch (ArgumentException)
        {
            _dialogService.InfoBarMessage(Translator.RemoteContentRules_AddFailedTitle, Translator.RemoteContentRules_InvalidPattern, InfoBarMessageType.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add a remote content sender rule.");
            _dialogService.InfoBarMessage(Translator.RemoteContentRules_AddFailedTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task RemoveRuleAsync(RemoteContentRuleViewModel? rule)
    {
        if (rule == null)
            return;

        try
        {
            await _ruleStore.DeleteAsync(rule.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete a remote content sender rule.");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private void ShowAll()
    {
        Filter = string.Empty;
        PolicyFilter = null;
    }

    private async Task ChangePolicyAsync(RemoteContentRuleViewModel row, RemoteContentSenderPolicy policy)
    {
        var current = row.Rule;

        try
        {
            await _ruleStore.UpsertAsync(new RemoteContentSenderRule
            {
                Id = current.Id,
                Pattern = current.Pattern,
                Kind = current.Kind,
                Policy = policy,
                Source = current.Source,
                CreatedUtc = current.CreatedUtc,
                LastAppliedUtc = current.LastAppliedUtc
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to change the policy of a remote content sender rule.");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);

            await ExecuteUIThread(row.SyncPolicy);
        }
    }

    private async Task ReloadAsync()
    {
        await _refreshGate.WaitAsync();

        try
        {
            await ExecuteUIThread(() => IsLoading = true);

            var rules = await _ruleStore.GetAllAsync();
            var rows = new List<RemoteContentRuleViewModel>(rules.Count);

            foreach (var rule in rules)
            {
                rows.Add(await CreateRowAsync(rule));
            }

            await ExecuteUIThread(() =>
            {
                _allRules.Clear();
                _allRules.AddRange(rows);

                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load remote content sender rules.");
        }
        finally
        {
            await ExecuteUIThread(() => IsLoading = false);
            _refreshGate.Release();
        }
    }

    private async Task RefreshPatternAsync(string? pattern)
    {
        if (pattern == null)
        {
            await ReloadAsync();
            return;
        }

        await _refreshGate.WaitAsync();

        try
        {
            var rule = await _ruleStore.GetByPatternAsync(pattern);
            var existing = _allRules.FirstOrDefault(row => row.Pattern == pattern);
            var created = rule != null && existing == null ? await CreateRowAsync(rule) : null;

            await ExecuteUIThread(() =>
            {
                if (rule == null)
                {
                    if (existing != null)
                        _allRules.Remove(existing);
                }
                else if (existing != null)
                {
                    existing.Update(rule);
                }
                else if (created != null)
                {
                    _allRules.Insert(0, created);
                }

                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh a remote content sender rule.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<RemoteContentRuleViewModel> CreateRowAsync(RemoteContentSenderRule rule)
    {
        string? contactName = null;

        if (rule.Kind == RemoteContentRuleKind.Address)
        {
            try
            {
                var contact = await _contactService.GetContactByAddressAsync(null, rule.Pattern);
                contactName = contact?.DisplayName;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to look up the contact for a remote content sender rule.");
            }
        }

        return new RemoteContentRuleViewModel(rule, contactName, PolicyOptions, ChangePolicyAsync, RemoveRuleAsync);
    }

    private void ApplyFilter()
    {
        var visible = _allRules.Where(row => row.Matches(Filter, PolicyFilter)).ToList();

        if (!visible.SequenceEqual(Rules))
        {
            Rules.Clear();

            foreach (var row in visible)
            {
                Rules.Add(row);
            }
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(IsShowingSubset));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowingText));
    }
}
