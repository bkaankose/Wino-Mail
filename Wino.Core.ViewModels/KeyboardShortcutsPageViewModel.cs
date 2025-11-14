using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;

namespace Wino.Core.ViewModels;

/// <summary>
/// ViewModel for managing keyboard shortcuts settings.
/// </summary>
public partial class KeyboardShortcutsPageViewModel : CoreBaseViewModel
{
    private readonly IKeyboardShortcutService _keyboardShortcutService;
    private readonly IMailDialogService _dialogService;

    [ObservableProperty]
    public partial ObservableCollection<KeyboardShortcutViewModel> Shortcuts { get; set; } = new();

    public KeyboardShortcutsPageViewModel(IKeyboardShortcutService keyboardShortcutService,
                                        IMailDialogService dialogService)
    {
        _keyboardShortcutService = keyboardShortcutService;
        _dialogService = dialogService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        await LoadShortcutsAsync();
    }

    [RelayCommand]
    private async Task LoadShortcutsAsync()
    {
        try
        {
            var keyboardShortcuts = await _keyboardShortcutService.GetKeyboardShortcutsAsync();

            Shortcuts.Clear();
            foreach (var shortcut in keyboardShortcuts)
            {
                Shortcuts.Add(new KeyboardShortcutViewModel(shortcut));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load keyboard shortcuts.", ex);

            await _dialogService.ShowMessageAsync(
                Translator.KeyboardShortcuts_FailedToLoad,
                Translator.GeneralTitle_Error,
                WinoCustomMessageDialogIcon.Error);
        }
    }

    [RelayCommand]
    private async Task StartAddingShortcutAsync()
    {
        var result = await _dialogService.ShowKeyboardShortcutDialogAsync();
        if (result.IsSuccess)
        {
            try
            {
                // Check if key combination is already in use
                var isInUse = await _keyboardShortcutService.IsKeyCombinationInUseAsync(result.Key, result.ModifierKeys, null);
                if (isInUse)
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_ShortcutInUse, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

                // Create new shortcut
                var shortcut = new KeyboardShortcut
                {
                    Key = result.Key,
                    ModifierKeys = result.ModifierKeys,
                    MailOperation = result.MailOperation,
                    IsEnabled = true
                };

                await _keyboardShortcutService.SaveKeyboardShortcutAsync(shortcut);
                await LoadShortcutsAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save new keyboard shortcut.", ex);
                await _dialogService.ShowMessageAsync(
                    Translator.KeyboardShortcuts_FailedToSave,
                    Translator.GeneralTitle_Error,
                    WinoCustomMessageDialogIcon.Error);
            }
        }
    }

    [RelayCommand]
    private async Task StartEditingShortcutAsync(KeyboardShortcutViewModel shortcut)
    {
        if (shortcut == null) return;

        var dialogService = _dialogService as IMailDialogService;
        if (dialogService == null) return;

        var existingShortcut = shortcut.ToEntity();
        var result = await dialogService.ShowKeyboardShortcutDialogAsync(existingShortcut);

        if (result.IsSuccess)
        {
            try
            {
                // Check if key combination is already in use (excluding current shortcut)
                var isInUse = await _keyboardShortcutService.IsKeyCombinationInUseAsync(result.Key, result.ModifierKeys, shortcut.Id);
                if (isInUse)
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_ShortcutInUse, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

                // Update existing shortcut
                var updatedShortcut = shortcut.ToEntity();
                updatedShortcut.Key = result.Key;
                updatedShortcut.ModifierKeys = result.ModifierKeys;
                updatedShortcut.MailOperation = result.MailOperation;

                await _keyboardShortcutService.SaveKeyboardShortcutAsync(updatedShortcut);
                await LoadShortcutsAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to update keyboard shortcut.", ex);

                await _dialogService.ShowMessageAsync(
                    Translator.KeyboardShortcuts_FailedToUpdate,
                    Translator.GeneralTitle_Error,
                    WinoCustomMessageDialogIcon.Error);
            }
        }
    }



    [RelayCommand]
    private async Task DeleteShortcutAsync(KeyboardShortcutViewModel shortcut)
    {
        if (shortcut == null) return;

        try
        {
            await _keyboardShortcutService.DeleteKeyboardShortcutAsync(shortcut.Id);
            await LoadShortcutsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to delete keyboard shortcut.", ex);
            await _dialogService.ShowMessageAsync(
                Translator.KeyboardShortcuts_FailedToDelete,
                Translator.GeneralTitle_Error,
                WinoCustomMessageDialogIcon.Error);
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        try
        {
            await _keyboardShortcutService.ResetToDefaultShortcutsAsync();
            await LoadShortcutsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to reset keyboard shortcuts to defaults.", ex);
            await _dialogService.ShowMessageAsync(
                Translator.KeyboardShortcuts_FailedToReset,
                Translator.GeneralTitle_Error,
                WinoCustomMessageDialogIcon.Error);
        }
    }

}
