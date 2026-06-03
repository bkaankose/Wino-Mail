using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

public partial class MessageListPageViewModel : MailBaseViewModel
{
    public IPreferencesService PreferencesService { get; }
    private readonly IThumbnailService _thumbnailService;
    private readonly IStatePersistanceService _statePersistenceService;
    private readonly IDialogServiceBase _dialogService;

    private readonly List<MailOperation> availableHoverActions =
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

    public List<string> AvailableHoverActionsTranslations { get; set; } =
    [
        Translator.HoverActionOption_Archive,
        Translator.HoverActionOption_Delete,
        Translator.HoverActionOption_ToggleFlag,
        Translator.HoverActionOption_ToggleRead,
        Translator.HoverActionOption_MoveJunk
    ];

    public List<string> ThreadItemSortingOptions { get; } =
    [
        Translator.SettingsThreadOrder_LastItemFirst,
        Translator.SettingsThreadOrder_FirstItemFirst
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

    public IMailItemDisplayInformation DemoPreviewMailItemInformation { get; } = new DemoMailItemDisplayInformation();

    public MailListDisplayMode SelectedMailSpacingMode => availableMailSpacingOptions[selectedMailSpacingIndex];

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
                OnPropertyChanged(nameof(SelectedMailSpacingMode));
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

    #region Properties
    private int leftHoverActionIndex;
    public int LeftHoverActionIndex
    {
        get => leftHoverActionIndex;
        set
        {
            if (SetProperty(ref leftHoverActionIndex, value))
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
            if (SetProperty(ref centerHoverActionIndex, value))
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
            if (SetProperty(ref rightHoverActionIndex, value))
            {
                PreferencesService.RightHoverAction = availableHoverActions[value];
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
        selectedMailSpacingIndex = availableMailSpacingOptions.IndexOf(PreferencesService.MailItemDisplayMode);
        SelectedMarkAsOptionIndex = Array.IndexOf(Enum.GetValues<MailMarkAsOption>(), PreferencesService.MarkAsPreference);
        selectedThreadItemSortingIndex = PreferencesService.IsNewestThreadMailFirst ? 0 : 1;
        selectedAccountNicknamePositionIndex = accountNicknamePositions.IndexOf(PreferencesService.AccountNicknamePosition);

        if (selectedAccountNicknamePositionIndex < 0)
        {
            selectedAccountNicknamePositionIndex = accountNicknamePositions.IndexOf(AccountNicknamePosition.Right);
        }
    }

    [RelayCommand]
    private async Task ClearAvatarsCacheAsync()
    {
        await _thumbnailService.ClearCache();
    }

    private sealed class DemoMailItemDisplayInformation : IMailItemDisplayInformation
    {
        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }

        public string Subject => "Quarterly planning notes";
        public string FromName => "Ava Brooks";
        public string FromAddress => "ava@contoso.com";
        public string PreviewText => "Agenda draft, attendee updates, and a few follow-up items for this week.";
        public bool IsRead => false;
        public bool IsDraft => false;
        public bool HasAttachments => true;
        public bool IsCalendarEvent => false;
        public bool IsFlagged => true;
        public DateTime CreationDate => DateTime.Now.AddMinutes(-12);
        public Guid? ContactPictureFileId => null;
        public bool ThumbnailUpdatedEvent => false;
        public bool IsThreadExpanded => false;
        public bool HasReadReceiptTracking => true;
        public bool IsReadReceiptAcknowledged => false;
        public string ReadReceiptDisplayText => Translator.MailReceiptStatus_Requested;
        public string AccountNickname => "Personal";
        public string AccountColorHex => "#00FF00";
        public AccountNicknamePosition AccountNicknamePosition => Wino.Core.Domain.Enums.AccountNicknamePosition.Right;
        public IReadOnlyList<MailCategory> Categories =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Urgent",
                BackgroundColorHex = "#FFE1DE",
                TextColorHex = "#A1260D"
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Client",
                BackgroundColorHex = "#E4E8FF",
                TextColorHex = "#4255C5"
            }
        ];
        public bool HasCategories => Categories.Count > 0;
        public AccountContact SenderContact => new()
        {
            Address = "hi@bkaan.dev",
            Name = "Burak Kaan Köse"
        };
    }
}
