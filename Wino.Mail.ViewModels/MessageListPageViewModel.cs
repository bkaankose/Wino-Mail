using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

public partial class MessageListPageViewModel : MailBaseViewModel
{
    public IPreferencesService PreferencesService { get; }
    private readonly INativeAppService _nativeAppService;

    public IRelayCommand ClearGravatarCacheAsync { get; }
    public IRelayCommand ClearFaviconCacheAsync { get; }

    private int selectedMarkAsOptionIndex;
    public int SelectedMarkAsOptionIndex
    {
        get => selectedMarkAsOptionIndex;
        set
        {
            if (SetProperty(ref selectedMarkAsOptionIndex, value))
            {
                if (value >= 0)
                {
                    PreferencesService.MarkAsPreference = (MailMarkAsOption)Enum.GetValues<MailMarkAsOption>().GetValue(value);
                }
            }
        }
    }

    private readonly List<MailOperation> availableHoverActions =
    [
        MailOperation.Archive,
        MailOperation.SoftDelete,
        MailOperation.SetFlag,
        MailOperation.MarkAsRead,
        MailOperation.MoveToJunk
    ];

    public List<string> AvailableHoverActionsTranslations { get; set; } =
    [
        Translator.HoverActionOption_Archive,
        Translator.HoverActionOption_Delete,
        Translator.HoverActionOption_ToggleFlag,
        Translator.HoverActionOption_ToggleRead,
        Translator.HoverActionOption_MoveJunk
    ];

    public bool IsShowSenderPicturesEnabledBindable
    {
        get => PreferencesService.IsShowSenderPicturesEnabled;
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

    public MessageListPageViewModel(IMailDialogService dialogService,
                                    IPreferencesService preferencesService,
                                    INativeAppService nativeAppService)
    {
        PreferencesService = preferencesService;
        _nativeAppService = nativeAppService;
        leftHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.LeftHoverAction);
        centerHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.CenterHoverAction);
        rightHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.RightHoverAction);
        SelectedMarkAsOptionIndex = Array.IndexOf(Enum.GetValues<MailMarkAsOption>(), PreferencesService.MarkAsPreference);
        ClearGravatarCacheAsync = new AsyncRelayCommand(ClearGravatarCacheAsyncImpl);
        ClearFaviconCacheAsync = new AsyncRelayCommand(ClearFaviconCacheAsyncImpl);

        if (PreferencesService is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PreferencesService.IsShowSenderPicturesEnabled))
                {
                    OnPropertyChanged(nameof(IsShowSenderPicturesEnabledBindable));
                }
            };
        }
    }

    private async Task ClearGravatarCacheAsyncImpl()
    {
        var cacheDir = await _nativeAppService.GetThumbnailStoragePath();
        foreach (var file in Directory.GetFiles(cacheDir, "*.gravatar", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
    }

    private async Task ClearFaviconCacheAsyncImpl()
    {
        var cacheDir = await _nativeAppService.GetThumbnailStoragePath();
        foreach (var file in Directory.GetFiles(cacheDir, "*.favicon", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
    }
}
