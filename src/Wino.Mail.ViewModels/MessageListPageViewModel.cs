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
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class MessageListPageViewModel : MailBaseViewModel
{
    public IPreferencesService PreferencesService { get; }
    private readonly IThumbnailService _thumbnailService;
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
                PreferencesService.LeftHoverAction = availableHoverActions[value];
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
                PreferencesService.CenterHoverAction = availableHoverActions[value];
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
                PreferencesService.RightHoverAction = availableHoverActions[value];
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
                PreferencesService.LeftSwipeOperation = availableSwipeActions[value];
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
                PreferencesService.RightSwipeOperation = availableSwipeActions[value];
            }
        }
    }
    #endregion

    public MessageListPageViewModel(IPreferencesService preferencesService,
                                    IThumbnailService thumbnailService,
                                    IStatePersistanceService statePersistenceService,
                                    IDialogServiceBase dialogService)
    {
        PreferencesService = preferencesService;
        _thumbnailService = thumbnailService;
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
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        PreviewMailCollection.CoreDispatcher = Dispatcher;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        PreferencesService.PreferenceChanged += PreferencesServiceChanged;

        await RefreshPreviewAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
    }

    private async void PreferencesServiceChanged(object sender, string propertyName)
    {
        if (!IsPreviewAffectingPreference(propertyName)) return;

        await RefreshPreviewAsync();
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

    private async Task RefreshPreviewAsync()
    {
        PreviewMailListOptions = new MailListProjectionOptions
        {
            SortMode = MailListSortMode.Date,
            GroupMode = MailListGroupMode.None,
            IsThreadingEnabled = PreferencesService.IsThreadingEnabled,
            ThreadMessageOrder = PreferencesService.IsNewestThreadMailFirst
                ? ThreadMessageOrder.NewestFirst
                : ThreadMessageOrder.OldestFirst,
            IsPinnedFirst = true,
        };

        await PreviewMailCollection.ClearAsync();
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
