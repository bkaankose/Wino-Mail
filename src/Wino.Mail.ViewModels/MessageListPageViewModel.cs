using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class MessageListPageViewModel : MailBaseViewModel,
    IRecipient<PreferenceChangedMessage>
{
    public IPreferencesService PreferencesService { get; }
    private readonly IThumbnailService _thumbnailService;
    private readonly IAccountService _accountService;
    private readonly List<MailAccount> _scopeAccounts = [];
    private bool _isSyncingScope;
    private readonly IStatePersistanceService _statePersistenceService;
    private readonly IDialogServiceBase _dialogService;

    private readonly List<MailOperation> availableHoverActions =
    [
        MailOperation.None,
        MailOperation.Archive,
        MailOperation.SoftDelete,
        MailOperation.SetFlag,
        MailOperation.MarkAsRead,
        MailOperation.MoveToJunk
    ];

    private readonly List<MailOperation> availableSwipeActions =
    [
        MailOperation.Archive,
        MailOperation.SoftDelete,
        MailOperation.SetFlag,
        MailOperation.MarkAsRead,
        MailOperation.MoveToJunk
    ];

    private readonly List<MailListDisplayMode> availableMailSpacingOptions =
    [
        MailListDisplayMode.Compact,
        MailListDisplayMode.Medium,
        MailListDisplayMode.Spacious
    ];

    private readonly List<TimeFormatPreference> timeFormatPreferenceOptions =
    [
        TimeFormatPreference.UseLanguageCulture,
        TimeFormatPreference.TwelveHour,
        TimeFormatPreference.TwentyFourHour
    ];

    public List<string> AvailableHoverActionsTranslations { get; set; } =
    [
        Translator.HoverActionOption_None,
        Translator.HoverActionOption_Archive,
        Translator.HoverActionOption_Delete,
        Translator.HoverActionOption_ToggleFlag,
        Translator.HoverActionOption_ToggleRead,
        Translator.HoverActionOption_MoveJunk
    ];

    public List<string> AvailableSwipeActionsTranslations { get; set; } =
    [
        Translator.HoverActionOption_Archive,
        Translator.HoverActionOption_Delete,
        Translator.HoverActionOption_ToggleFlag,
        Translator.HoverActionOption_ToggleRead,
        Translator.HoverActionOption_MoveJunk
    ];

    private readonly List<MailHoverActionAnimation> hoverActionAnimations =
    [
        MailHoverActionAnimation.Popup,
        MailHoverActionAnimation.Slide,
        MailHoverActionAnimation.NoAnimation
    ];

    private readonly List<MailHoverActionPosition> hoverActionPositions =
    [
        MailHoverActionPosition.RightCenter,
        MailHoverActionPosition.RightTop,
        MailHoverActionPosition.RightBottom,
        MailHoverActionPosition.TopCenter,
        MailHoverActionPosition.BottomCenter
    ];

    private readonly List<MailHoverActionButtonSize> hoverActionButtonSizes =
    [
        MailHoverActionButtonSize.Small,
        MailHoverActionButtonSize.Medium,
        MailHoverActionButtonSize.Large
    ];

    public List<string> HoverActionAnimationOptions { get; } =
    [
        Translator.HoverActionAnimation_Popup,
        Translator.HoverActionAnimation_Slide,
        Translator.HoverActionAnimation_None
    ];

    public List<string> HoverActionPositionOptions { get; } =
    [
        Translator.HoverActionPosition_RightCenter,
        Translator.HoverActionPosition_RightTop,
        Translator.HoverActionPosition_RightBottom,
        Translator.HoverActionPosition_TopCenter,
        Translator.HoverActionPosition_BottomCenter
    ];

    public List<string> HoverActionButtonSizeOptions { get; } =
    [
        Translator.HoverActionButtonSize_Small,
        Translator.HoverActionButtonSize_Medium,
        Translator.HoverActionButtonSize_Large
    ];

    public List<string> ThreadItemSortingOptions { get; } =
    [
        Translator.SettingsThreadOrder_LastItemFirst,
        Translator.SettingsThreadOrder_FirstItemFirst
    ];

    public List<string> TimeFormatPreferenceOptions { get; } =
    [
        Translator.SettingsTimeFormat_UseLanguageCulture,
        Translator.SettingsTimeFormat_TwelveHour,
        Translator.SettingsTimeFormat_TwentyFourHour
    ];

    public List<string> AccountNicknamePositionOptions { get; } =
    [
        Translator.AccountNicknamePosition_None,
        Translator.AccountNicknamePosition_Right,
        Translator.AccountNicknamePosition_Left
    ];

    private readonly List<AccountNicknamePosition> accountNicknamePositions =
[
    AccountNicknamePosition.None,
        AccountNicknamePosition.Right,
        AccountNicknamePosition.Left
];

    private static readonly Guid PreviewMailUniqueId = new("0F5C3B31-0F52-4E4E-9D45-1B7B3F4E5A01");
    private static readonly Guid PreviewFolderId = new("0F5C3B31-0F52-4E4E-9D45-1B7B3F4E5A02");
    private static readonly Guid PreviewAccountId = new("0F5C3B31-0F52-4E4E-9D45-1B7B3F4E5A03");
    private static readonly Guid PreviewUrgentCategoryId = new("0F5C3B31-0F52-4E4E-9D45-1B7B3F4E5A04");
    private static readonly Guid PreviewClientCategoryId = new("0F5C3B31-0F52-4E4E-9D45-1B7B3F4E5A05");

    public List<string> MailSpacingOptions { get; } =
    [
        Translator.SettingsPersonalizationMailDisplayCompactMode,
        Translator.SettingsPersonalizationMailDisplayMediumMode,
        Translator.SettingsPersonalizationMailDisplaySpaciousMode
    ];

    /// <summary>
    /// Single item list that feeds the preview row on top of the page. The preview is rendered by the
    /// same list control and the same templates the mail list uses, so it reflects the real thing.
    /// </summary>
    public MailListStore PreviewMailCollection { get; } = new();
    private readonly object _previewGate = new();
    private volatile bool _previewActive;
    public Task PreviewRefreshTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial MailListProjectionOptions PreviewMailListOptions { get; set; } = new();

    private int selectedAccountNicknamePositionIndex;
    public int SelectedAccountNicknamePositionIndex
    {
        get => selectedAccountNicknamePositionIndex;
        set
        {
            if (SetProperty(ref selectedAccountNicknamePositionIndex, value) && value >= 0 && value < accountNicknamePositions.Count)
            {
                PreferencesService.AccountNicknamePosition = accountNicknamePositions[value];
            }
        }
    }

    private int selectedMarkAsOptionIndex;
    public int SelectedMarkAsOptionIndex
    {
        get => selectedMarkAsOptionIndex;
        set
        {
            if (SetProperty(ref selectedMarkAsOptionIndex, value) && value >= 0)
            {
                PreferencesService.MarkAsPreference = (MailMarkAsOption)Enum.GetValues<MailMarkAsOption>().GetValue(value);
            }
        }
    }

    private int selectedMailSpacingIndex;
    public int SelectedMailSpacingIndex
    {
        get => selectedMailSpacingIndex;
        set
        {
            if (SetProperty(ref selectedMailSpacingIndex, value) && value >= 0 && value < availableMailSpacingOptions.Count)
            {
                PreferencesService.MailItemDisplayMode = availableMailSpacingOptions[value];
            }
        }
    }

    private int selectedThreadItemSortingIndex;
    public int SelectedThreadItemSortingIndex
    {
        get => selectedThreadItemSortingIndex;
        set
        {
            if (SetProperty(ref selectedThreadItemSortingIndex, value) && value >= 0)
            {
                PreferencesService.IsNewestThreadMailFirst = value == 0;
            }
        }
    }

    private int selectedTimeFormatPreferenceIndex;
    public int SelectedTimeFormatPreferenceIndex
    {
        get => selectedTimeFormatPreferenceIndex;
        set
        {
            if (SetProperty(ref selectedTimeFormatPreferenceIndex, value) && value >= 0 && value < timeFormatPreferenceOptions.Count)
            {
                PreferencesService.MailTimeFormatPreference = timeFormatPreferenceOptions[value];
            }
        }
    }

    private int selectedHoverActionAnimationIndex;
    public int SelectedHoverActionAnimationIndex
    {
        get => selectedHoverActionAnimationIndex;
        set
        {
            if (SetProperty(ref selectedHoverActionAnimationIndex, value) && value >= 0 && value < hoverActionAnimations.Count)
            {
                PreferencesService.HoverActionAnimation = hoverActionAnimations[value];
            }
        }
    }

    private int selectedHoverActionPositionIndex;
    public int SelectedHoverActionPositionIndex
    {
        get => selectedHoverActionPositionIndex;
        set
        {
            if (SetProperty(ref selectedHoverActionPositionIndex, value) && value >= 0 && value < hoverActionPositions.Count)
            {
                PreferencesService.HoverActionPosition = hoverActionPositions[value];
            }
        }
    }

    private int selectedHoverActionButtonSizeIndex;
    public int SelectedHoverActionButtonSizeIndex
    {
        get => selectedHoverActionButtonSizeIndex;
        set
        {
            if (SetProperty(ref selectedHoverActionButtonSizeIndex, value) && value >= 0 && value < hoverActionButtonSizes.Count)
            {
                PreferencesService.HoverActionButtonSize = hoverActionButtonSizes[value];
            }
        }
    }

    #region Properties
    private int leftHoverActionIndex;
    public int LeftHoverActionIndex
    {
        get => leftHoverActionIndex;
        set
        {
            if (SetProperty(ref leftHoverActionIndex, value) && IsValidHoverActionIndex(value))
            {
                OnHoverActionIndexChanged();
            }
        }
    }

    private int centerHoverActionIndex;
    public int CenterHoverActionIndex
    {
        get => centerHoverActionIndex;
        set
        {
            if (SetProperty(ref centerHoverActionIndex, value) && IsValidHoverActionIndex(value))
            {
                OnHoverActionIndexChanged();
            }
        }
    }

    private int rightHoverActionIndex;
    public int RightHoverActionIndex
    {
        get => rightHoverActionIndex;
        set
        {
            if (SetProperty(ref rightHoverActionIndex, value) && IsValidHoverActionIndex(value))
            {
                OnHoverActionIndexChanged();
            }
        }
    }

    private int leftSwipeActionIndex;
    public int LeftSwipeActionIndex
    {
        get => leftSwipeActionIndex;
        set
        {
            if (SetProperty(ref leftSwipeActionIndex, value) && IsValidSwipeActionIndex(value))
            {
                OnSwipeActionIndexChanged();
            }
        }
    }

    private int rightSwipeActionIndex;
    public int RightSwipeActionIndex
    {
        get => rightSwipeActionIndex;
        set
        {
            if (SetProperty(ref rightSwipeActionIndex, value) && IsValidSwipeActionIndex(value))
            {
                OnSwipeActionIndexChanged();
            }
        }
    }
    #endregion

    #region Per-account scope

    /// <summary>"All accounts" first, then one entry per mail account, in the order of <see cref="_scopeAccounts"/>.</summary>
    [ObservableProperty]
    public partial List<string> ActionScopeOptions { get; set; } = [Translator.SettingsActionScope_AllAccounts];

    /// <summary>Hidden with a single account unless that account still carries an override to undo.</summary>
    [ObservableProperty]
    public partial bool IsAccountScopeAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedHoverScopeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedSwipeScopeIndex { get; set; }

    [ObservableProperty]
    public partial string HoverScopeDescription { get; set; } = Translator.SettingsActionScope_AllAccountsDescription;

    [ObservableProperty]
    public partial string SwipeScopeDescription { get; set; } = Translator.SettingsActionScope_AllAccountsDescription;

    [ObservableProperty]
    public partial bool CanUseDefaultHoverActions { get; set; }

    [ObservableProperty]
    public partial bool CanUseDefaultSwipeActions { get; set; }

    /// <summary>Size, position and animation are app-wide, so they lock while an account is selected.</summary>
    [ObservableProperty]
    public partial bool IsHoverPresentationEditable { get; set; }

    [ObservableProperty]
    public partial string? HoverPresentationDescription { get; set; }

    [ObservableProperty]
    public partial bool HasCustomisedHoverAccounts { get; set; }

    [ObservableProperty]
    public partial string CustomisedHoverAccountsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCustomisedSwipeAccounts { get; set; }

    [ObservableProperty]
    public partial string CustomisedSwipeAccountsText { get; set; } = string.Empty;

    /// <summary>The preview row follows whichever scope is selected in each expander.</summary>
    [ObservableProperty]
    public partial MailListActionResolver? PreviewActionResolver { get; set; }

    private MailAccount? SelectedHoverAccount => GetScopeAccount(SelectedHoverScopeIndex);

    private MailAccount? SelectedSwipeAccount => GetScopeAccount(SelectedSwipeScopeIndex);

    private MailAccount? GetScopeAccount(int scopeIndex)
        => scopeIndex > 0 && scopeIndex <= _scopeAccounts.Count ? _scopeAccounts[scopeIndex - 1] : null;

    partial void OnSelectedHoverScopeIndexChanged(int value) => SyncHoverScope();

    partial void OnSelectedSwipeScopeIndexChanged(int value) => SyncSwipeScope();

    private async Task LoadScopeAccountsAsync()
    {
        var accounts = (await _accountService.GetAccountsAsync()).Where(account => account.Preferences != null).ToList();

        await ExecuteUIThread(() =>
        {
            _scopeAccounts.Clear();
            _scopeAccounts.AddRange(accounts);

            var options = new List<string> { Translator.SettingsActionScope_AllAccounts };
            options.AddRange(accounts.Select(GetScopeDisplayName));
            ActionScopeOptions = options;

            var defaultHover = MailListActionPreferences.GetDefaultHoverActions(PreferencesService);
            var defaultSwipe = MailListActionPreferences.GetDefaultSwipeActions(PreferencesService);

            IsAccountScopeAvailable = accounts.Count > 1 || accounts.Any(account =>
                MailListActionPreferences.HasCustomHoverActions(account.Preferences, defaultHover) ||
                MailListActionPreferences.HasCustomSwipeActions(account.Preferences, defaultSwipe));

            SelectedHoverScopeIndex = 0;
            SelectedSwipeScopeIndex = 0;
            SyncHoverScope();
            SyncSwipeScope();
        });
    }

    private static string GetScopeDisplayName(MailAccount account)
        => string.IsNullOrWhiteSpace(account.Address) || string.Equals(account.Name, account.Address, StringComparison.OrdinalIgnoreCase)
            ? account.Name
            : $"{account.Name} ({account.Address})";

    private void SyncHoverScope()
    {
        var actions = MailListActionPreferences.ResolveHoverActions(PreferencesService, SelectedHoverAccount?.Preferences);

        _isSyncingScope = true;

        try
        {
            LeftHoverActionIndex = Math.Max(0, availableHoverActions.IndexOf(actions.Left));
            CenterHoverActionIndex = Math.Max(0, availableHoverActions.IndexOf(actions.Center));
            RightHoverActionIndex = Math.Max(0, availableHoverActions.IndexOf(actions.Right));
        }
        finally
        {
            _isSyncingScope = false;
        }

        RefreshHoverScopeState();
    }

    private void SyncSwipeScope()
    {
        var actions = MailListActionPreferences.ResolveSwipeActions(PreferencesService, SelectedSwipeAccount?.Preferences);

        _isSyncingScope = true;

        try
        {
            LeftSwipeActionIndex = Math.Max(0, availableSwipeActions.IndexOf(actions.Left));
            RightSwipeActionIndex = Math.Max(0, availableSwipeActions.IndexOf(actions.Right));
        }
        finally
        {
            _isSyncingScope = false;
        }

        RefreshSwipeScopeState();
    }

    private void RefreshHoverScopeState()
    {
        var defaults = MailListActionPreferences.GetDefaultHoverActions(PreferencesService);
        var account = SelectedHoverAccount;
        var isCustomised = MailListActionPreferences.HasCustomHoverActions(account?.Preferences, defaults);

        CanUseDefaultHoverActions = isCustomised;
        IsHoverPresentationEditable = account is null && PreferencesService.IsHoverActionsEnabled;
        HoverPresentationDescription = account is null ? null : Translator.SettingsActionScope_PresentationFollowsAllAccounts;
        HoverScopeDescription = GetScopeDescription(account, isCustomised);

        var customisedNames = string.Join(", ", _scopeAccounts
            .Where(scopeAccount => MailListActionPreferences.HasCustomHoverActions(scopeAccount.Preferences, defaults))
            .Select(scopeAccount => scopeAccount.Name));

        HasCustomisedHoverAccounts = customisedNames.Length > 0;
        CustomisedHoverAccountsText = HasCustomisedHoverAccounts
            ? string.Format(Translator.SettingsHoverActions_CustomisedAccounts, customisedNames)
            : string.Empty;

        RefreshPreviewActionResolver();
    }

    private void RefreshSwipeScopeState()
    {
        var defaults = MailListActionPreferences.GetDefaultSwipeActions(PreferencesService);
        var account = SelectedSwipeAccount;
        var isCustomised = MailListActionPreferences.HasCustomSwipeActions(account?.Preferences, defaults);

        CanUseDefaultSwipeActions = isCustomised;
        SwipeScopeDescription = GetScopeDescription(account, isCustomised);

        var customisedNames = string.Join(", ", _scopeAccounts
            .Where(scopeAccount => MailListActionPreferences.HasCustomSwipeActions(scopeAccount.Preferences, defaults))
            .Select(scopeAccount => scopeAccount.Name));

        HasCustomisedSwipeAccounts = customisedNames.Length > 0;
        CustomisedSwipeAccountsText = HasCustomisedSwipeAccounts
            ? string.Format(Translator.SettingsSwipeActions_CustomisedAccounts, customisedNames)
            : string.Empty;

        RefreshPreviewActionResolver();
    }

    private static string GetScopeDescription(MailAccount? account, bool isCustomised)
        => account is null
            ? Translator.SettingsActionScope_AllAccountsDescription
            : string.Format(isCustomised ? Translator.SettingsActionScope_CustomisedDescription : Translator.SettingsActionScope_FollowingDescription, account.Name);

    private void RefreshPreviewActionResolver()
    {
        var hover = MailListActionPreferences.ResolveHoverActions(PreferencesService, SelectedHoverAccount?.Preferences);
        var swipe = MailListActionPreferences.ResolveSwipeActions(PreferencesService, SelectedSwipeAccount?.Preferences);

        PreviewActionResolver = new MailListActionResolver(hover, swipe);
    }

    private void OnHoverActionIndexChanged()
    {
        if (_isSyncingScope) return;

        _ = ObserveScopeSaveAsync(ApplyHoverActionsAsync());
    }

    private void OnSwipeActionIndexChanged()
    {
        if (_isSyncingScope) return;

        _ = ObserveScopeSaveAsync(ApplySwipeActionsAsync());
    }

    private static async Task ObserveScopeSaveAsync(Task save)
    {
        try { await save; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Failed to save mail list actions."); }
    }

    private async Task ApplyHoverActionsAsync()
    {
        if (!IsValidHoverActionIndex(leftHoverActionIndex) || !IsValidHoverActionIndex(centerHoverActionIndex) || !IsValidHoverActionIndex(rightHoverActionIndex))
            return;

        var actions = new HoverMailActions(
            availableHoverActions[leftHoverActionIndex],
            availableHoverActions[centerHoverActionIndex],
            availableHoverActions[rightHoverActionIndex]);

        if (SelectedHoverAccount is { Preferences: { } preferences } account)
        {
            MailListActionPreferences.SetCustomHoverActions(preferences, actions, MailListActionPreferences.GetDefaultHoverActions(PreferencesService));
            await SaveAccountActionsAsync(account);
        }
        else
        {
            PreferencesService.LeftHoverAction = actions.Left;
            PreferencesService.CenterHoverAction = actions.Center;
            PreferencesService.RightHoverAction = actions.Right;

            await DropMatchingOverridesAsync();
        }

        RefreshHoverScopeState();
    }

    private async Task ApplySwipeActionsAsync()
    {
        if (!IsValidSwipeActionIndex(leftSwipeActionIndex) || !IsValidSwipeActionIndex(rightSwipeActionIndex))
            return;

        var actions = new SwipeMailActions(availableSwipeActions[leftSwipeActionIndex], availableSwipeActions[rightSwipeActionIndex]);

        if (SelectedSwipeAccount is { Preferences: { } preferences } account)
        {
            MailListActionPreferences.SetCustomSwipeActions(preferences, actions, MailListActionPreferences.GetDefaultSwipeActions(PreferencesService));
            await SaveAccountActionsAsync(account);
        }
        else
        {
            PreferencesService.LeftSwipeOperation = actions.Left;
            PreferencesService.RightSwipeOperation = actions.Right;

            await DropMatchingOverridesAsync();
        }

        RefreshSwipeScopeState();
    }

    /// <summary>
    /// An override equal to the new app-wide actions is no override. Dropping it keeps the account
    /// following future changes instead of silently freezing it on a copy.
    /// </summary>
    private async Task DropMatchingOverridesAsync()
    {
        var defaultHover = MailListActionPreferences.GetDefaultHoverActions(PreferencesService);
        var defaultSwipe = MailListActionPreferences.GetDefaultSwipeActions(PreferencesService);

        foreach (var account in _scopeAccounts)
        {
            if (account.Preferences is not { } preferences) continue;

            var dropHover = preferences.HasCustomHoverActions && MailListActionPreferences.GetCustomHoverActions(preferences) == defaultHover;
            var dropSwipe = preferences.HasCustomSwipeActions && MailListActionPreferences.GetCustomSwipeActions(preferences) == defaultSwipe;

            if (!dropHover && !dropSwipe) continue;

            if (dropHover) preferences.HasCustomHoverActions = false;
            if (dropSwipe) preferences.HasCustomSwipeActions = false;

            await SaveAccountActionsAsync(account);
        }
    }

    private async Task SaveAccountActionsAsync(MailAccount account)
    {
        if (account.Preferences is not { } preferences) return;

        await _accountService.UpdateAccountPreferencesAsync(preferences);
        Messenger.Send(new AccountMailActionsUpdated(account.Id));
    }

    [RelayCommand]
    private async Task UseDefaultHoverActionsAsync()
    {
        if (SelectedHoverAccount is not { Preferences: { } preferences } account) return;

        preferences.HasCustomHoverActions = false;
        await SaveAccountActionsAsync(account);
        SyncHoverScope();
    }

    [RelayCommand]
    private async Task UseDefaultSwipeActionsAsync()
    {
        if (SelectedSwipeAccount is not { Preferences: { } preferences } account) return;

        preferences.HasCustomSwipeActions = false;
        await SaveAccountActionsAsync(account);
        SyncSwipeScope();
    }

    #endregion

    public MessageListPageViewModel(IPreferencesService preferencesService,
                                    IThumbnailService thumbnailService,
                                    IStatePersistanceService statePersistenceService,
                                    IDialogServiceBase dialogService,
                                    IAccountService accountService)
    {
        PreferencesService = preferencesService;
        _thumbnailService = thumbnailService;
        _accountService = accountService;
        _statePersistenceService = statePersistenceService;
        _dialogService = dialogService;
        leftHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.LeftHoverAction);
        centerHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.CenterHoverAction);
        rightHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.RightHoverAction);
        leftSwipeActionIndex = availableSwipeActions.IndexOf(PreferencesService.LeftSwipeOperation);
        rightSwipeActionIndex = availableSwipeActions.IndexOf(PreferencesService.RightSwipeOperation);
        selectedMailSpacingIndex = availableMailSpacingOptions.IndexOf(PreferencesService.MailItemDisplayMode);
        SelectedMarkAsOptionIndex = Array.IndexOf(Enum.GetValues<MailMarkAsOption>(), PreferencesService.MarkAsPreference);
        selectedThreadItemSortingIndex = PreferencesService.IsNewestThreadMailFirst ? 0 : 1;
        selectedAccountNicknamePositionIndex = accountNicknamePositions.IndexOf(PreferencesService.AccountNicknamePosition);
        selectedTimeFormatPreferenceIndex = timeFormatPreferenceOptions.IndexOf(PreferencesService.MailTimeFormatPreference);
        selectedHoverActionAnimationIndex = hoverActionAnimations.IndexOf(PreferencesService.HoverActionAnimation);
        selectedHoverActionPositionIndex = hoverActionPositions.IndexOf(PreferencesService.HoverActionPosition);
        selectedHoverActionButtonSizeIndex = hoverActionButtonSizes.IndexOf(PreferencesService.HoverActionButtonSize);

        if (leftHoverActionIndex < 0)
        {
            leftHoverActionIndex = availableHoverActions.IndexOf(MailOperation.Archive);
        }

        if (centerHoverActionIndex < 0)
        {
            centerHoverActionIndex = availableHoverActions.IndexOf(MailOperation.SoftDelete);
        }

        if (rightHoverActionIndex < 0)
        {
            rightHoverActionIndex = availableHoverActions.IndexOf(MailOperation.SetFlag);
        }

        if (leftSwipeActionIndex < 0)
        {
            leftSwipeActionIndex = availableSwipeActions.IndexOf(MailOperation.SoftDelete);
        }

        if (rightSwipeActionIndex < 0)
        {
            rightSwipeActionIndex = availableSwipeActions.IndexOf(MailOperation.MarkAsRead);
        }

        if (selectedAccountNicknamePositionIndex < 0)
        {
            selectedAccountNicknamePositionIndex = accountNicknamePositions.IndexOf(AccountNicknamePosition.Right);
        }

        if (selectedTimeFormatPreferenceIndex < 0)
        {
            selectedTimeFormatPreferenceIndex = timeFormatPreferenceOptions.IndexOf(TimeFormatPreference.UseLanguageCulture);
        }

        if (selectedHoverActionAnimationIndex < 0)
        {
            selectedHoverActionAnimationIndex = hoverActionAnimations.IndexOf(MailHoverActionAnimation.Popup);
        }

        if (selectedHoverActionPositionIndex < 0)
        {
            selectedHoverActionPositionIndex = hoverActionPositions.IndexOf(MailHoverActionPosition.RightCenter);
        }

        if (selectedHoverActionButtonSizeIndex < 0)
        {
            selectedHoverActionButtonSizeIndex = hoverActionButtonSizes.IndexOf(MailHoverActionButtonSize.Small);
        }

        PreviewMailCollection.MailItemFactory = mailCopy => new MailItemViewModel(mailCopy, PreferencesService.AccountNicknamePosition);
        IsHoverPresentationEditable = PreferencesService.IsHoverActionsEnabled;
        RefreshPreviewActionResolver();
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        PreviewMailCollection.CoreDispatcher = Dispatcher;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        => await InitializeNavigationAsync(mode, parameters);

    public async Task InitializeNavigationAsync(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _previewActive = true;

        await LoadScopeAccountsAsync();
        await RefreshPreviewAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        _previewActive = false;
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

    public void Receive(PreferenceChangedMessage message)
    {
        if (message.PropertyName == nameof(IPreferencesService.IsHoverActionsEnabled))
        {
            IsHoverPresentationEditable = SelectedHoverAccount is null && PreferencesService.IsHoverActionsEnabled;
            return;
        }

        if (!IsPreviewAffectingPreference(message.PropertyName)) return;

        _ = ObservePreviewRefreshAsync(RefreshPreviewAsync());
    }
    private static async Task ObservePreviewRefreshAsync(Task refresh)
    {
        try { await refresh; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Failed to refresh message-list settings preview."); }
    }

    /// <summary>
    /// The row templates read these preferences with OneTime bindings and x:Load, so the only way to
    /// reflect a change is to realize the container again.
    /// </summary>
    private static bool IsPreviewAffectingPreference(string propertyName) => propertyName is
        nameof(IPreferencesService.MailItemDisplayMode) or
        nameof(IPreferencesService.IsShowSenderPicturesEnabled) or
        nameof(IPreferencesService.IsGravatarEnabled) or
        nameof(IPreferencesService.IsFaviconEnabled) or
        nameof(IPreferencesService.IsShowPreviewEnabled) or
        nameof(IPreferencesService.MailTimeFormatPreference) or
        nameof(IPreferencesService.AccountNicknamePosition) or
        nameof(IPreferencesService.IsThreadingEnabled) or
        nameof(IPreferencesService.IsNewestThreadMailFirst);

    private Task RefreshPreviewAsync()
    {
        lock (_previewGate)
        {
            PreviewRefreshTask = RefreshPreviewCoreAsync(PreviewRefreshTask);
            return PreviewRefreshTask;
        }
    }
    private async Task RefreshPreviewCoreAsync(Task previous)
    {
        // Accepted refreshes drain in order. A later preference change can recover
        // a failed preview, while callers can still observe each task's outcome.
        try { await previous; } catch (Exception) { }
        if (!_previewActive) return;
        await ExecuteUIThread(() => PreviewMailListOptions = new MailListProjectionOptions
        {
            SortMode = MailListSortMode.Date,
            GroupMode = MailListGroupMode.None,
            IsThreadingEnabled = PreferencesService.IsThreadingEnabled,
            ThreadMessageOrder = PreferencesService.IsNewestThreadMailFirst
                ? ThreadMessageOrder.NewestFirst
                : ThreadMessageOrder.OldestFirst,
            IsPinnedFirst = true,
        });

        await PreviewMailCollection.ClearAsync();
        if (!_previewActive) return;
        await PreviewMailCollection.AddAsync(CreatePreviewMailCopy());
    }

    /// <summary>
    /// Preview only interaction. Hover buttons flip the state of the demo row and never reach any service.
    /// </summary>
    [RelayCommand]
    private void ExecutePreviewHoverAction(HoverActionCommandRequest request)
    {
        var previewItem = request?.Row?.LeafItems.OfType<MailItemViewModel>().FirstOrDefault();
        if (previewItem == null) return;

        switch (request.Action)
        {
            case HoverActionKind.ToggleRead:
                previewItem.IsRead = !previewItem.IsRead;
                break;
            case HoverActionKind.ToggleFlag:
                previewItem.IsFlagged = !previewItem.IsFlagged;
                break;
        }
    }

    [RelayCommand]
    private async Task ClearAvatarsCacheAsync()
    {
        await _thumbnailService.ClearCache();
    }

    private bool IsValidHoverActionIndex(int index) => index >= 0 && index < availableHoverActions.Count;

    private bool IsValidSwipeActionIndex(int index) => index >= 0 && index < availableSwipeActions.Count;

    /// <summary>
    /// The row the preview card shows. Values are picked so every previewed setting has something to
    /// act on: an unread flagged mail with an attachment, a preview line, categories and an account
    /// nickname.
    /// </summary>
    private static MailCopy CreatePreviewMailCopy() => new()
    {
        UniqueId = PreviewMailUniqueId,
        Id = "preview-mail",
        FolderId = PreviewFolderId,
        FileId = Guid.Empty,
        ThreadId = "preview-thread",
        MessageId = "preview-message",
        Subject = "Quarterly planning notes",
        PreviewText = "Agenda draft, attendee updates, and a few follow-up items for this week.",
        FromName = "Ava Brooks",
        FromAddress = "ava@contoso.com",
        CreationDate = DateTime.Now.AddMinutes(-12),
        IsRead = false,
        IsFlagged = true,
        HasAttachments = true,
        ItemType = MailItemType.Mail,
        IsReadReceiptRequested = true,
        ReadReceiptStatus = SentMailReceiptStatus.Requested,
        SenderContact = new AccountContact
        {
            Address = "ava@contoso.com",
            Name = "Ava Brooks"
        },
        AssignedAccount = new MailAccount
        {
            Id = PreviewAccountId,
            Name = "Personal",
            Address = "me@contoso.com",
            AccountColorHex = "#00FF00"
        },
        Categories =
        [
            new()
            {
                Id = PreviewUrgentCategoryId,
                Name = "Urgent",
                BackgroundColorHex = "#FFE1DE",
                TextColorHex = "#A1260D"
            },
            new()
            {
                Id = PreviewClientCategoryId,
                Name = "Client",
                BackgroundColorHex = "#E4E8FF",
                TextColorHex = "#4255C5"
            }
        ]
    };
}
