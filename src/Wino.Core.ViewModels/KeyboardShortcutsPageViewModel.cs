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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool IsEmpty => !IsLoading && !HasError && Shortcuts.Count == 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

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
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var keyboardShortcuts = await _keyboardShortcutService.GetKeyboardShortcutsAsync();

            Shortcuts.Clear();
            foreach (var shortcut in keyboardShortcuts)
            {
                Shortcuts.Add(new KeyboardShortcutViewModel(shortcut));
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load keyboard shortcuts.", ex);
            ErrorMessage = Translator.KeyboardShortcuts_FailedToLoad;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
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
                if (_keyboardShortcutService.IsReservedShortcut(result.Mode, result.Key, result.ModifierKeys))
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_ReservedUndoShortcut, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

                // Check if key combination is already in use
                var isInUse = await _keyboardShortcutService.IsKeyCombinationInUseAsync(result.Mode, result.Key, result.ModifierKeys, null);
                if (isInUse)
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_ShortcutInUse, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

                // Create new shortcut
                var shortcut = new KeyboardShortcut
                {
                    Mode = result.Mode,
                    Key = result.Key,
                    ModifierKeys = result.ModifierKeys,
                    Action = result.Action,
                    IsEnabled = true
                };

                if (!_keyboardShortcutService.IsShortcutAllowed(shortcut))
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_InvalidShortcut, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

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
                if (_keyboardShortcutService.IsReservedShortcut(result.Mode, result.Key, result.ModifierKeys))
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_ReservedUndoShortcut, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

                // Check if key combination is already in use (excluding current shortcut)
                var isInUse = await _keyboardShortcutService.IsKeyCombinationInUseAsync(result.Mode, result.Key, result.ModifierKeys, shortcut.Id);
                if (isInUse)
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_ShortcutInUse, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

                // Update existing shortcut
                var updatedShortcut = shortcut.ToEntity();
                updatedShortcut.Mode = result.Mode;
                updatedShortcut.Key = result.Key;
                updatedShortcut.ModifierKeys = result.ModifierKeys;
                updatedShortcut.Action = result.Action;

                if (!_keyboardShortcutService.IsShortcutAllowed(updatedShortcut))
                {
                    await _dialogService.ShowMessageAsync(Translator.KeyboardShortcuts_InvalidShortcut, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
                    return;
                }

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
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                Translator.KeyboardShortcuts_ResetConfirmationMessage,
                Translator.KeyboardShortcuts_ResetConfirmationTitle,
                Translator.KeyboardShortcuts_ResetToDefaults);
            if (!confirmed)
                return;

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

    [RelayCommand]
    private async Task ToggleShortcutAsync(KeyboardShortcutViewModel shortcut)
    {
        if (shortcut is null)
            return;

        try
        {
            await _keyboardShortcutService.UpdateKeyboardShortcutEnabledAsync(shortcut.Id, shortcut.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update keyboard shortcut enabled state.", ex);
            shortcut.IsEnabled = !shortcut.IsEnabled;
            await _dialogService.ShowMessageAsync(
                Translator.KeyboardShortcuts_FailedToUpdate,
                Translator.GeneralTitle_Error,
                WinoCustomMessageDialogIcon.Error);
        }
    }

}
