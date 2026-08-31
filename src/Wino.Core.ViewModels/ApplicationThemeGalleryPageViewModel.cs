using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class ApplicationThemeGalleryPageViewModel : CoreBaseViewModel, IBackNavigationAware
{
    private readonly INewThemeService _themeService;
    private readonly IDialogServiceBase _dialogService;
    private List<AppThemeBase> _allThemes = [];
    private AppThemeBase? _failedTheme;

    public ObservableCollection<AppThemeBase> FilteredThemes { get; } = [];

    [ObservableProperty] public partial AppThemeBase? CurrentTheme { get; set; }
    [ObservableProperty] public partial ThemeGalleryFilter SelectedFilter { get; set; } = ThemeGalleryFilter.All;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsStorageError { get; set; }
    [ObservableProperty] public partial bool IsApplyError { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyThemeCommand))] public partial bool IsApplying { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    public bool IsOnline => SelectedFilter == ThemeGalleryFilter.Online;
    public bool IsLocalGalleryVisible => !IsOnline;
    public bool IsEmpty => !IsOnline && !IsLoading && !IsStorageError && FilteredThemes.Count == 0;
    public int SelectedFilterIndex
    {
        get => (int)SelectedFilter;
        set => SelectedFilter = Enum.IsDefined(typeof(ThemeGalleryFilter), value)
            ? (ThemeGalleryFilter)value
            : ThemeGalleryFilter.All;
    }

    public ApplicationThemeGalleryPageViewModel(INewThemeService themeService, IDialogServiceBase dialogService)
    {
        _themeService = themeService;
        _dialogService = dialogService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (mode != NavigationMode.Back)
            SelectedFilter = ThemeGalleryFilter.All;

        _ = LoadThemesAsync();
    }

    public void OnNavigatedBack(object? parameter, NavigationResult? result)
    {
        if (result?.Kind == NavigationResultKind.Saved)
            _ = LoadThemesAsync();
    }

    partial void OnSelectedFilterChanged(ThemeGalleryFilter value)
    {
        OnPropertyChanged(nameof(SelectedFilterIndex));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsLocalGalleryVisible));
        RefreshFilter();
    }

    [RelayCommand]
    private async Task LoadThemesAsync()
    {
        IsLoading = true;
        IsStorageError = false;
        IsApplyError = false;

        try
        {
            _allThemes = await _themeService.GetAvailableThemesAsync();
            CurrentTheme = _themeService.CurrentApplicationThemeId is Guid currentId
                ? _allThemes.FirstOrDefault(theme => theme.Id == currentId)
                : _allThemes.FirstOrDefault();
            RefreshFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsStorageError = true;
            FilteredThemes.Clear();
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private bool CanApplyTheme(AppThemeBase? theme) => theme != null && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanApplyTheme))]
    private async Task ApplyThemeAsync(AppThemeBase? theme)
    {
        if (theme == null)
            return;

        IsApplyError = false;
        IsApplying = true;

        try
        {
            _failedTheme = null;
            await _themeService.SelectThemeAsync(theme.Id);
            CurrentTheme = theme;
            RefreshFilter();
        }
        catch (Exception ex)
        {
            _failedTheme = theme;
            ErrorMessage = string.IsNullOrWhiteSpace(ex.Message)
                ? Translator.ApplicationThemeGallery_ApplyError
                : ex.Message;
            IsApplyError = true;
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private Task RetryApplyAsync() => ApplyThemeAsync(_failedTheme);

    [RelayCommand]
    private void CreateTheme() => NavigateEditor(null);

    [RelayCommand]
    private void EditTheme(AppThemeBase? theme)
    {
        if (theme?.AppThemeType == AppThemeType.Custom)
            NavigateEditor(theme.Id);
    }

    [RelayCommand]
    private async Task RemoveThemeAsync(AppThemeBase? theme)
    {
        if (theme?.AppThemeType != AppThemeType.Custom)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.SettingsCustomTheme_DeleteConfirm_Message, theme.ThemeName),
            Translator.SettingsCustomTheme_DeleteConfirm_Title,
            Translator.Buttons_Delete);

        if (!confirmed)
            return;

        try
        {
            if (!await _themeService.DeleteCustomThemeAsync(theme.Id))
                throw new InvalidOperationException(Translator.SettingsCustomTheme_DeleteMissing);

            await LoadThemesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsStorageError = true;
        }
    }

    private void NavigateEditor(Guid? themeId)
        => WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(
            themeId.HasValue ? Translator.ApplicationThemeEditor_EditTitle : Translator.ApplicationThemeEditor_CreateTitle,
            WinoPage.ApplicationThemeEditorPage,
            new CustomThemeEditorNavigationParameter(themeId)));

    private void RefreshFilter()
    {
        FilteredThemes.Clear();
        var currentId = CurrentTheme?.Id ?? _themeService.CurrentApplicationThemeId;

        foreach (var theme in ThemeGalleryFilterPolicy.Apply(_allThemes, currentId, SelectedFilter))
            FilteredThemes.Add(theme);

        OnPropertyChanged(nameof(IsEmpty));
    }
}
