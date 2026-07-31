using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels;

public partial class MailNotificationSettingsPageViewModel : MailBaseViewModel
{
    private static readonly MailOperation[] SupportedMailNotificationActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.MoveToJunk,
        MailOperation.Archive,
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

    private readonly IPreferencesService _preferencesService;
    private bool _isUpdatingSelection;
    private bool _isLoaded;

    public ObservableCollection<MailNotificationActionOption> AvailableNotificationActions { get; } = [];

    [ObservableProperty]
    public partial MailNotificationActionOption SelectedFirstAction { get; set; }

    [ObservableProperty]
    public partial MailNotificationActionOption SelectedSecondAction { get; set; }

    public MailNotificationSettingsPageViewModel(IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;

        foreach (var action in SupportedMailNotificationActions)
        {
            AvailableNotificationActions.Add(new MailNotificationActionOption(action, GetOperationDisplayText(action)));
        }

        InitializeSelections();
        _isLoaded = true;
    }

    partial void OnSelectedFirstActionChanged(MailNotificationActionOption value)
    {
        if (!_isLoaded || _isUpdatingSelection || value == null)
            return;

        EnsureDistinctSelections(changedSelection: value, isFirstSelection: true);
        _preferencesService.FirstMailNotificationAction = value.Operation;
    }

    partial void OnSelectedSecondActionChanged(MailNotificationActionOption value)
    {
        if (!_isLoaded || _isUpdatingSelection || value == null)
            return;

        EnsureDistinctSelections(changedSelection: value, isFirstSelection: false);
        _preferencesService.SecondMailNotificationAction = value.Operation;
    }

    private void InitializeSelections()
    {
        var firstAction = ResolveSupportedAction(_preferencesService.FirstMailNotificationAction, MailOperation.MarkAsRead);
        var secondAction = ResolveSupportedAction(_preferencesService.SecondMailNotificationAction, MailOperation.SoftDelete);

        if (secondAction == firstAction)
        {
            secondAction = GetFallbackDistinctAction(firstAction);
        }

        SelectedFirstAction = GetOption(firstAction);
        SelectedSecondAction = GetOption(secondAction);

        _preferencesService.FirstMailNotificationAction = firstAction;
        _preferencesService.SecondMailNotificationAction = secondAction;
    }

    private void EnsureDistinctSelections(MailNotificationActionOption changedSelection, bool isFirstSelection)
    {
        var otherSelection = isFirstSelection ? SelectedSecondAction : SelectedFirstAction;
        if (otherSelection?.Operation != changedSelection.Operation)
            return;

        _isUpdatingSelection = true;

        var fallbackAction = GetFallbackDistinctAction(changedSelection.Operation);
        var fallbackOption = GetOption(fallbackAction);

        if (isFirstSelection)
        {
            SelectedSecondAction = fallbackOption;
            _preferencesService.SecondMailNotificationAction = fallbackAction;
        }
        else
        {
            SelectedFirstAction = fallbackOption;
            _preferencesService.FirstMailNotificationAction = fallbackAction;
        }

        _isUpdatingSelection = false;
    }

    private MailNotificationActionOption GetOption(MailOperation action)
        => AvailableNotificationActions.First(option => option.Operation == action);

    private static MailOperation ResolveSupportedAction(MailOperation action, MailOperation fallbackAction)
        => SupportedMailNotificationActions.Contains(action) ? action : fallbackAction;

    private static MailOperation GetFallbackDistinctAction(MailOperation excludedAction)
        => SupportedMailNotificationActions.First(action => action != excludedAction);

    private static string GetOperationDisplayText(MailOperation action)
        => action switch
        {
            MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
            MailOperation.SoftDelete => Translator.MailOperation_Delete,
            MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
            MailOperation.Archive => Translator.MailOperation_Archive,
            MailOperation.Reply => Translator.MailOperation_Reply,
            MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
            MailOperation.Forward => Translator.MailOperation_Forward,
            _ => action.ToString()
        };
}

public sealed class MailNotificationActionOption(MailOperation operation, string displayText)
{
    public MailOperation Operation { get; } = operation;
    public string DisplayText { get; } = displayText;
}
