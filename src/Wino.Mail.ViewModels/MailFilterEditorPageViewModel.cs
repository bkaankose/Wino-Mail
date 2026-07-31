using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

public sealed record MailFilterEditorNavigationParameter(
    Guid AccountId,
    Guid? FilterId = null,
    Guid? DuplicateFilterId = null);

public sealed record MailFilterManagementOption(MailFilterManagementType Value, string DisplayName);
public sealed record MailFilterMatchOption(MailFilterMatchMode Value, string DisplayName);
public sealed record MailFilterConditionFieldOption(MailFilterConditionField Value, string DisplayName);
public sealed record MailFilterConditionOperatorOption(MailFilterConditionOperator Value, string DisplayName);
public sealed record MailFilterActionOption(MailFilterActionType Value, string DisplayName);

public sealed record MailFilterFolderOption(
    string RemoteFolderId,
    string DisplayName,
    SpecialFolderType SpecialFolderType)
{
    public override string ToString() => DisplayName;
}

public sealed record MailFilterValueChoiceOption(string Value, string DisplayName);

public partial class MailFilterConditionEditorItem : ObservableObject
{
    private static readonly IReadOnlyList<MailFilterValueChoiceOption> BooleanChoices =
    [
        new(bool.TrueString, Translator.Buttons_Yes),
        new(bool.FalseString, Translator.Buttons_No)
    ];

    private static readonly IReadOnlyList<MailFilterValueChoiceOption> ImportanceChoices =
    [
        new(nameof(MailImportance.Low), Translator.MailFilterImportance_Low),
        new(nameof(MailImportance.Normal), Translator.MailFilterImportance_Normal),
        new(nameof(MailImportance.High), Translator.MailFilterImportance_High)
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoiceField))]
    [NotifyPropertyChangedFor(nameof(IsTextField))]
    [NotifyPropertyChangedFor(nameof(ChoiceOptions))]
    [NotifyPropertyChangedFor(nameof(AvailableOperators))]
    public partial MailFilterConditionFieldOption SelectedField { get; set; }

    [ObservableProperty]
    public partial MailFilterConditionOperatorOption SelectedOperator { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MailFilterValueChoiceOption SelectedChoice { get; set; }

    [ObservableProperty]
    public partial string ConjunctionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowConjunction { get; set; }

    public IReadOnlyList<MailFilterConditionFieldOption> FieldOptions { get; init; }
    public IReadOnlyList<MailFilterConditionOperatorOption> OperatorOptions { get; init; }

    public bool IsChoiceField => IsChoice(SelectedField?.Value);
    public bool IsTextField => !IsChoiceField;

    public IReadOnlyList<MailFilterValueChoiceOption> ChoiceOptions => SelectedField?.Value switch
    {
        MailFilterConditionField.HasAttachments => BooleanChoices,
        MailFilterConditionField.Importance => ImportanceChoices,
        _ => []
    };

    public IReadOnlyList<MailFilterConditionOperatorOption> AvailableOperators
        => IsChoiceField
            ? OperatorOptions?.Where(option => option.Value is MailFilterConditionOperator.Equals
                or MailFilterConditionOperator.NotEquals).ToList() ?? []
            : OperatorOptions ?? [];

    private static bool IsChoice(MailFilterConditionField? field)
        => field is MailFilterConditionField.HasAttachments or MailFilterConditionField.Importance;

    partial void OnSelectedFieldChanged(MailFilterConditionFieldOption oldValue, MailFilterConditionFieldOption newValue)
    {
        // Only reset dependent selections on user-driven field changes, not during initial hydration.
        if (oldValue == null)
            return;

        if (IsChoiceField)
        {
            if (SelectedOperator?.Value is not MailFilterConditionOperator.Equals
                and not MailFilterConditionOperator.NotEquals)
            {
                SelectedOperator = AvailableOperators.FirstOrDefault();
            }

            var selectedChoice = ChoiceOptions.FirstOrDefault(choice =>
                string.Equals(choice.Value, Value, StringComparison.OrdinalIgnoreCase))
                ?? ChoiceOptions.FirstOrDefault();

            SelectedChoice = selectedChoice;
            Value = selectedChoice?.Value ?? string.Empty;
        }
        else if (IsChoice(oldValue.Value))
        {
            SelectedChoice = null;
            Value = string.Empty;
        }
    }

    partial void OnSelectedChoiceChanged(MailFilterValueChoiceOption value)
    {
        if (value != null)
            Value = value.Value;
    }
}

public partial class MailFilterActionEditorItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsTargetFolder))]
    [NotifyPropertyChangedFor(nameof(IsDestructive))]
    public partial MailFilterActionOption SelectedAction { get; set; }

    [ObservableProperty]
    public partial MailFilterFolderOption SelectedTargetFolder { get; set; }

    public bool NeedsTargetFolder => SelectedAction?.Value == MailFilterActionType.Move;
    public bool IsDestructive => SelectedAction?.Value == MailFilterActionType.HardDelete;

    public IReadOnlyList<MailFilterActionOption> ActionOptions { get; init; }
    public IReadOnlyList<MailFilterFolderOption> FolderOptions { get; init; }
}

public partial class MailFilterEditorPageViewModel(
    IMailFilterService mailFilterService,
    IMailFilterProviderService providerService,
    IAccountService accountService,
    IFolderService folderService,
    IMailDialogService dialogService,
    INavigationService navigationService) : MailBaseViewModel
{
    private readonly IMailFilterService _mailFilterService = mailFilterService;
    private readonly IMailFilterProviderService _providerService = providerService;
    private readonly IAccountService _accountService = accountService;
    private readonly IFolderService _folderService = folderService;
    private readonly IMailDialogService _dialogService = dialogService;
    private MailFilter _editingFilter;

    public INavigationService NavigationService { get; } = navigationService;

    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial string FilterName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWinoManaged))]
    [NotifyPropertyChangedFor(nameof(IsProviderSelected))]
    [NotifyPropertyChangedFor(nameof(CanAddAction))]
    public partial MailFilterManagementOption SelectedManagementType { get; set; }

    [ObservableProperty]
    public partial MailFilterMatchOption SelectedMatchMode { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MailFilterFolderOption SelectedSourceFolder { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool StopProcessing { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    public ObservableCollection<MailFilterConditionEditorItem> Conditions { get; } = [];
    public ObservableCollection<MailFilterActionEditorItem> Actions { get; } = [];
    public ObservableCollection<MailFilterFolderOption> Folders { get; } = [];
    public bool CanChangeManagementType => _editingFilter == null;
    public bool IsWinoManaged => SelectedManagementType?.Value == MailFilterManagementType.WinoLocal;
    public bool IsProviderSelected => SelectedManagementType?.Value == MailFilterManagementType.Provider;
    public bool IsProviderAvailable => ManagementTypes.Any(option => option.Value == MailFilterManagementType.Provider);
    public bool CanAddAction => !IsWinoManaged || !Actions.Any(action =>
        IsExclusiveLocalAction(action.SelectedAction?.Value));

    public int SelectedMatchModeIndex
    {
        get => SelectedMatchMode?.Value == MailFilterMatchMode.Any ? 1 : 0;
        set
        {
            if (value >= 0 && value < MatchModes.Count)
                SelectedMatchMode = MatchModes[value];
        }
    }

    public IReadOnlyList<MailFilterManagementOption> ManagementTypes { get; private set; } = [];
    public IReadOnlyList<MailFilterMatchOption> MatchModes { get; } =
    [
        new(MailFilterMatchMode.All, Translator.MailFilterEditor_MatchAll),
        new(MailFilterMatchMode.Any, Translator.MailFilterEditor_MatchAny)
    ];
    public IReadOnlyList<MailFilterConditionFieldOption> ConditionFields { get; } =
    [
        new(MailFilterConditionField.FromAddress, Translator.MailFilterField_FromAddress),
        new(MailFilterConditionField.FromName, Translator.MailFilterField_FromName),
        new(MailFilterConditionField.Subject, Translator.MailFilterField_Subject),
        new(MailFilterConditionField.PreviewText, Translator.MailFilterField_PreviewText),
        new(MailFilterConditionField.HasAttachments, Translator.MailFilterField_HasAttachments),
        new(MailFilterConditionField.Importance, Translator.MailFilterField_Importance)
    ];
    public IReadOnlyList<MailFilterConditionOperatorOption> ConditionOperators { get; } =
    [
        new(MailFilterConditionOperator.Equals, Translator.MailFilterOperator_Equals),
        new(MailFilterConditionOperator.NotEquals, Translator.MailFilterOperator_NotEquals),
        new(MailFilterConditionOperator.Contains, Translator.MailFilterOperator_Contains),
        new(MailFilterConditionOperator.NotContains, Translator.MailFilterOperator_NotContains),
        new(MailFilterConditionOperator.StartsWith, Translator.MailFilterOperator_StartsWith),
        new(MailFilterConditionOperator.EndsWith, Translator.MailFilterOperator_EndsWith)
    ];
    public IReadOnlyList<MailFilterActionOption> ActionTypes { get; } =
    [
        new(MailFilterActionType.MarkRead, Translator.MailFilterAction_MarkRead),
        new(MailFilterActionType.MarkUnread, Translator.MailFilterAction_MarkUnread),
        new(MailFilterActionType.SetFlag, Translator.MailFilterAction_SetFlag),
        new(MailFilterActionType.ClearFlag, Translator.MailFilterAction_ClearFlag),
        new(MailFilterActionType.Move, Translator.MailFilterAction_Move),
        new(MailFilterActionType.Archive, Translator.MailFilterAction_Archive),
        new(MailFilterActionType.MoveToJunk, Translator.MailFilterAction_MoveToJunk),
        new(MailFilterActionType.MarkAsNotJunk, Translator.MailFilterAction_MarkAsNotJunk),
        new(MailFilterActionType.SoftDelete, Translator.MailFilterAction_SoftDelete),
        new(MailFilterActionType.HardDelete, Translator.MailFilterAction_HardDelete)
    ];

    private bool _isChangeTrackingAttached;
    private bool _isNormalizingActions;

    public async Task LoadAsync(object parameter)
    {
        if (parameter is not MailFilterEditorNavigationParameter context)
            return;

        EnsureChangeTracking();

        var account = await _accountService.GetAccountAsync(context.AccountId).ConfigureAwait(false);
        if (account == null)
            return;
        await ExecuteUIThread(() => Account = account);

        var folders = (await _folderService.GetFoldersAsync(Account.Id).ConfigureAwait(false))
            .Where(folder => folder.IsMoveTarget && !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .OrderBy(folder => folder.FolderName)
            .Select(folder => new MailFilterFolderOption(
                folder.RemoteFolderId,
                folder.FolderName,
                folder.SpecialFolderType))
            .ToList();
        var supportsProvider = _providerService.SupportsProviderFilters(account);
        var managementTypes = new List<MailFilterManagementOption>
        {
            new(MailFilterManagementType.WinoLocal, Translator.MailFilterEditor_WinoManaged)
        };
        if (supportsProvider)
            managementTypes.Insert(0, new(MailFilterManagementType.Provider, Translator.MailFilterEditor_ProviderManaged));

        MailFilter source = null;
        var isDuplicate = context.DuplicateFilterId.HasValue;
        if (context.FilterId.HasValue)
        {
            source = await _mailFilterService.GetFilterAsync(context.FilterId.Value).ConfigureAwait(false);
            _editingFilter = source;
        }
        else if (isDuplicate)
        {
            source = await _mailFilterService.GetFilterAsync(context.DuplicateFilterId.Value).ConfigureAwait(false);
        }

        await ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(CanChangeManagementType));
            ManagementTypes = managementTypes;
            OnPropertyChanged(nameof(ManagementTypes));
            OnPropertyChanged(nameof(IsProviderAvailable));
            Folders.Clear();
            foreach (var folder in folders)
                Folders.Add(folder);

            FilterName = source == null
                ? string.Empty
                : isDuplicate
                    ? string.Format(Translator.MailFilterEditor_CopyName, source.Name)
                    : source.Name;
            IsEnabled = source?.IsEnabled ?? true;
            StopProcessing = source?.StopProcessing ?? false;
            SelectedManagementType = ManagementTypes.FirstOrDefault(option =>
                option.Value == (source?.ManagementType ?? (supportsProvider
                    ? MailFilterManagementType.Provider
                    : MailFilterManagementType.WinoLocal)));
            SelectedMatchMode = MatchModes.First(option => option.Value == (source?.MatchMode ?? MailFilterMatchMode.All));
            SelectedSourceFolder = isDuplicate
                ? null
                : Folders.FirstOrDefault(folder =>
                    string.Equals(folder.RemoteFolderId, source?.SourceRemoteFolderId, StringComparison.OrdinalIgnoreCase));

            Conditions.Clear();
            foreach (var condition in source?.Conditions ?? [])
                Conditions.Add(CreateConditionItem(condition));
            if (Conditions.Count == 0)
                AddCondition();

            Actions.Clear();
            foreach (var action in source?.Actions ?? [])
                Actions.Add(CreateActionItem(action));
            if (Actions.Count == 0)
                AddAction();

            RefreshConjunctions();
            RefreshSummary();
        });
    }

    public void SelectManagementType(MailFilterManagementType type)
    {
        if (!CanChangeManagementType)
            return;

        var option = ManagementTypes.FirstOrDefault(candidate => candidate.Value == type);
        if (option != null)
            SelectedManagementType = option;
    }

    private void EnsureChangeTracking()
    {
        if (_isChangeTrackingAttached)
            return;

        _isChangeTrackingAttached = true;
        Conditions.CollectionChanged += (_, args) =>
        {
            UpdateItemSubscriptions(args);
            RefreshConjunctions();
            RefreshSummary();
        };
        Actions.CollectionChanged += (_, args) =>
        {
            UpdateItemSubscriptions(args);
            OnPropertyChanged(nameof(CanAddAction));
            RefreshSummary();
        };
    }

    private void UpdateItemSubscriptions(System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        foreach (var item in args.OldItems?.OfType<ObservableObject>() ?? [])
            item.PropertyChanged -= OnEditorItemPropertyChanged;
        foreach (var item in args.NewItems?.OfType<ObservableObject>() ?? [])
            item.PropertyChanged += OnEditorItemPropertyChanged;
    }

    private void OnEditorItemPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is MailFilterActionEditorItem actionItem
            && e.PropertyName == nameof(MailFilterActionEditorItem.SelectedAction))
        {
            EnforceExclusiveAction(actionItem);
            OnPropertyChanged(nameof(CanAddAction));
        }

        if (e.PropertyName is not (nameof(MailFilterConditionEditorItem.ConjunctionText))
            and not (nameof(MailFilterConditionEditorItem.ShowConjunction)))
        {
            RefreshSummary();
        }
    }

    partial void OnFilterNameChanged(string value) => RefreshSummary();

    partial void OnSelectedManagementTypeChanged(
        MailFilterManagementOption oldValue,
        MailFilterManagementOption newValue)
    {
        if (newValue?.Value == MailFilterManagementType.WinoLocal)
        {
            var exclusiveAction = Actions.FirstOrDefault(action =>
                IsExclusiveLocalAction(action.SelectedAction?.Value));
            if (exclusiveAction != null)
                EnforceExclusiveAction(exclusiveAction);
        }

        OnPropertyChanged(nameof(CanAddAction));
    }

    partial void OnSelectedMatchModeChanged(MailFilterMatchOption value)
    {
        OnPropertyChanged(nameof(SelectedMatchModeIndex));
        RefreshConjunctions();
        RefreshSummary();
    }

    private void RefreshConjunctions()
    {
        var conjunction = SelectedMatchMode?.Value == MailFilterMatchMode.Any
            ? Translator.MailFilterEditor_ConjunctionOr
            : Translator.MailFilterEditor_ConjunctionAnd;

        for (var i = 0; i < Conditions.Count; i++)
        {
            Conditions[i].ShowConjunction = i > 0;
            Conditions[i].ConjunctionText = conjunction;
        }
    }

    private void RefreshSummary()
    {
        var conditionJoiner = SelectedMatchMode?.Value == MailFilterMatchMode.Any
            ? Translator.MailFilterEditor_SummaryJoinerAny
            : Translator.MailFilterEditor_SummaryJoinerAll;
        var conditionText = string.Join(conditionJoiner, Conditions
            .Where(condition => condition.SelectedField != null && condition.SelectedOperator != null)
            .Select(condition => string.Format(
                Translator.MailFilterEditor_SummaryCondition,
                condition.SelectedField.DisplayName,
                condition.SelectedOperator.DisplayName,
                condition.IsChoiceField
                    ? condition.SelectedChoice?.DisplayName ?? "…"
                    : string.IsNullOrWhiteSpace(condition.Value) ? "…" : condition.Value.Trim())));

        var actionText = string.Join(Translator.MailFilterEditor_SummaryActionJoiner, Actions
            .Where(action => action.SelectedAction != null)
            .Select(action => action.NeedsTargetFolder && action.SelectedTargetFolder != null
                ? string.Format(
                    Translator.MailFilterEditor_SummaryActionWithFolder,
                    action.SelectedAction.DisplayName,
                    action.SelectedTargetFolder.DisplayName)
                : action.SelectedAction.DisplayName));

        Summary = string.IsNullOrEmpty(conditionText) || string.IsNullOrEmpty(actionText)
            ? string.Empty
            : string.Format(Translator.MailFilterEditor_SummaryFormat, conditionText, actionText);
    }

    public void AddCondition()
        => Conditions.Add(new MailFilterConditionEditorItem
        {
            FieldOptions = ConditionFields,
            OperatorOptions = ConditionOperators,
            SelectedField = ConditionFields.First(),
            SelectedOperator = ConditionOperators.First(option => option.Value == MailFilterConditionOperator.Contains)
        });

    public void RemoveCondition(MailFilterConditionEditorItem item)
    {
        if (item != null && Conditions.Count > 1)
            Conditions.Remove(item);
    }

    public void AddAction()
    {
        if (!CanAddAction)
            return;

        var selectedTypes = Actions
            .Where(action => action.SelectedAction != null)
            .Select(action => action.SelectedAction.Value)
            .ToHashSet();
        var defaultAction = ActionTypes.FirstOrDefault(option =>
            !IsExclusiveLocalAction(option.Value) && !selectedTypes.Contains(option.Value))
            ?? ActionTypes.First(option => !IsExclusiveLocalAction(option.Value));

        Actions.Add(new MailFilterActionEditorItem
        {
            ActionOptions = ActionTypes,
            FolderOptions = Folders,
            SelectedAction = defaultAction
        });
    }

    public void RemoveAction(MailFilterActionEditorItem item)
    {
        if (item != null && Actions.Count > 1)
            Actions.Remove(item);
    }

    private void EnforceExclusiveAction(MailFilterActionEditorItem selectedItem)
    {
        if (_isNormalizingActions
            || !IsWinoManaged
            || !IsExclusiveLocalAction(selectedItem?.SelectedAction?.Value))
        {
            return;
        }

        _isNormalizingActions = true;
        try
        {
            foreach (var otherAction in Actions.Where(action => action != selectedItem).ToList())
                Actions.Remove(otherAction);
        }
        finally
        {
            _isNormalizingActions = false;
        }
    }

    private static bool IsExclusiveLocalAction(MailFilterActionType? action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete
            or MailFilterActionType.HardDelete;

    public async Task SaveAsync()
    {
        if (Account == null || IsSaving)
            return;
        if (string.IsNullOrWhiteSpace(FilterName))
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.MailFilterEditor_NameRequired,
                InfoBarMessageType.Warning);
            return;
        }
        if (SelectedManagementType?.Value == MailFilterManagementType.WinoLocal && SelectedSourceFolder == null)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.MailFilterEditor_SourceRequired,
                InfoBarMessageType.Warning);
            return;
        }
        if (Conditions.Any(condition =>
                string.IsNullOrWhiteSpace(condition.Value)
                || (condition.SelectedField.Value is MailFilterConditionField.HasAttachments
                    or MailFilterConditionField.Importance)
                    && condition.SelectedOperator.Value is not MailFilterConditionOperator.Equals
                        and not MailFilterConditionOperator.NotEquals))
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.MailFilterEditor_ConditionInvalid,
                InfoBarMessageType.Warning);
            return;
        }

        var filter = BuildFilter();
        if (filter.Actions.Any(action =>
                RequiresTargetFolder(action.Type)
                && string.IsNullOrWhiteSpace(action.TargetRemoteFolderId)))
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.MailFilterEditor_TargetRequired,
                InfoBarMessageType.Warning);
            return;
        }
        if (filter.Actions.Any(action => action.Type == MailFilterActionType.HardDelete)
            && !await _dialogService.ShowHardDeleteConfirmationAsync())
        {
            return;
        }

        if (filter.ManagementType == MailFilterManagementType.Provider
            && !IsProviderCompatible(filter, out var reason))
        {
            _dialogService.InfoBarMessage(
                Translator.MailFilterEditor_ProviderUnsupportedTitle,
                reason,
                InfoBarMessageType.Warning);
            return;
        }

        await ExecuteUIThread(() => IsSaving = true);
        try
        {
            if (filter.ManagementType == MailFilterManagementType.Provider)
            {
                if (_editingFilter == null)
                    await _providerService.CreateFilterAsync(Account, filter).ConfigureAwait(false);
                else
                    await _providerService.UpdateFilterAsync(Account, filter).ConfigureAwait(false);
            }
            else if (_editingFilter == null)
            {
                await _mailFilterService.CreateFilterAsync(filter).ConfigureAwait(false);
            }
            else
            {
                await _mailFilterService.UpdateFilterAsync(filter).ConfigureAwait(false);
            }

            await ExecuteUIThread(() => NavigationService.GoBack());
        }
        catch (Exception ex)
        {
            await ExecuteUIThread(() =>
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error));
        }
        finally
        {
            await ExecuteUIThread(() => IsSaving = false);
        }
    }

    private MailFilter BuildFilter()
    {
        var filter = new MailFilter
        {
            Id = _editingFilter?.Id ?? Guid.NewGuid(),
            MailAccountId = Account.Id,
            RemoteId = _editingFilter?.RemoteId,
            CreatedAtUtc = _editingFilter?.CreatedAtUtc ?? DateTime.UtcNow,
            IsWinoCreated = _editingFilter?.IsWinoCreated ?? true,
            Name = FilterName.Trim(),
            ManagementType = SelectedManagementType.Value,
            SourceRemoteFolderId = SelectedManagementType.Value == MailFilterManagementType.WinoLocal
                ? SelectedSourceFolder?.RemoteFolderId
                : null,
            MatchMode = SelectedMatchMode.Value,
            IsEnabled = IsEnabled,
            StopProcessing = StopProcessing,
            Sequence = _editingFilter?.Sequence ?? 0,
            Conditions = Conditions.Select((item, index) => new MailFilterCondition
            {
                Order = index,
                Field = item.SelectedField.Value,
                Operator = item.SelectedOperator.Value,
                Value = item.Value?.Trim()
            }).ToList(),
            Actions = Actions.Select((item, index) => new MailFilterAction
            {
                Order = index,
                Type = item.SelectedAction.Value,
                TargetRemoteFolderId = ResolveTargetFolderId(item)
            }).ToList()
        };
        return filter;
    }

    private string ResolveTargetFolderId(MailFilterActionEditorItem item)
    {
        if (item.SelectedAction == null)
            return null;

        if (item.SelectedAction.Value == MailFilterActionType.Move)
            return item.SelectedTargetFolder?.RemoteFolderId;

        var specialFolderType = item.SelectedAction.Value switch
        {
            MailFilterActionType.Archive => SpecialFolderType.Archive,
            MailFilterActionType.MoveToJunk => SpecialFolderType.Junk,
            MailFilterActionType.MarkAsNotJunk => SpecialFolderType.Inbox,
            MailFilterActionType.SoftDelete => SpecialFolderType.Deleted,
            _ => SpecialFolderType.Other
        };
        return specialFolderType == SpecialFolderType.Other
            ? null
            : Folders.FirstOrDefault(folder => folder.SpecialFolderType == specialFolderType)?.RemoteFolderId;
    }

    private static bool RequiresTargetFolder(MailFilterActionType action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete;

    private bool IsProviderCompatible(MailFilter filter, out string reason)
    {
        if (Account.ProviderType == MailProviderType.Outlook)
        {
            var unsupportedAction = filter.Actions.FirstOrDefault(action =>
                action.Type is MailFilterActionType.MarkUnread
                    or MailFilterActionType.SetFlag
                    or MailFilterActionType.ClearFlag);
            var unsupportedCondition = filter.Conditions.FirstOrDefault(condition =>
                !IsOutlookConditionSupported(condition));
            if (filter.MatchMode != MailFilterMatchMode.All
                || unsupportedAction != null
                || unsupportedCondition != null)
            {
                reason = Translator.MailFilterEditor_OutlookUnsupported;
                return false;
            }
        }
        else if (Account.ProviderType == MailProviderType.Gmail)
        {
            var unsupportedAction = filter.Actions.FirstOrDefault(action => action.Type == MailFilterActionType.HardDelete);
            var unsupportedCondition = filter.Conditions.FirstOrDefault(condition =>
                !IsGmailConditionSupported(condition));
            if (filter.MatchMode != MailFilterMatchMode.All
                || filter.StopProcessing
                || unsupportedAction != null
                || unsupportedCondition != null)
            {
                reason = Translator.MailFilterEditor_GmailUnsupported;
                return false;
            }
        }
        else
        {
            reason = Translator.MailFilterEditor_ProviderUnavailable;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsOutlookConditionSupported(MailFilterCondition condition)
        => condition.Field switch
        {
            MailFilterConditionField.FromAddress => condition.Operator == MailFilterConditionOperator.Equals,
            MailFilterConditionField.FromName
                or MailFilterConditionField.Subject
                or MailFilterConditionField.PreviewText => condition.Operator == MailFilterConditionOperator.Contains,
            MailFilterConditionField.HasAttachments
                or MailFilterConditionField.Importance => condition.Operator == MailFilterConditionOperator.Equals,
            _ => false
        };

    private static bool IsGmailConditionSupported(MailFilterCondition condition)
        => condition.Field switch
        {
            MailFilterConditionField.FromAddress
                or MailFilterConditionField.Subject
                or MailFilterConditionField.PreviewText => condition.Operator == MailFilterConditionOperator.Contains,
            MailFilterConditionField.HasAttachments
                or MailFilterConditionField.Importance => condition.Operator == MailFilterConditionOperator.Equals,
            _ => false
        };

    private MailFilterConditionEditorItem CreateConditionItem(MailFilterCondition condition)
    {
        var item = new MailFilterConditionEditorItem
        {
            FieldOptions = ConditionFields,
            OperatorOptions = ConditionOperators,
            SelectedField = ConditionFields.First(option => option.Value == condition.Field),
            SelectedOperator = ConditionOperators.First(option => option.Value == condition.Operator),
            Value = condition.Value
        };

        if (item.IsChoiceField)
        {
            item.SelectedChoice = item.ChoiceOptions.FirstOrDefault(choice =>
                string.Equals(choice.Value, condition.Value, StringComparison.OrdinalIgnoreCase))
                ?? item.ChoiceOptions.First();
        }

        return item;
    }

    private MailFilterActionEditorItem CreateActionItem(MailFilterAction action)
        => new()
        {
            ActionOptions = ActionTypes,
            FolderOptions = Folders,
            SelectedAction = ActionTypes.First(option => option.Value == action.Type),
            SelectedTargetFolder = Folders.FirstOrDefault(folder =>
                string.Equals(folder.RemoteFolderId, action.TargetRemoteFolderId, StringComparison.OrdinalIgnoreCase))
        };
}
