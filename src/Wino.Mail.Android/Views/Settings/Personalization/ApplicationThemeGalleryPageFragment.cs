using Android.Views;
using Android.Widget;
using Google.Android.Material.ProgressIndicator;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.Personalization;

/// <summary>
/// Windows ApplicationThemeGalleryPage (iOS ApplicationThemeGalleryPageViewController): storage and apply error bars
/// with Retry, the current theme with "Create custom theme", the All/Dark/Light/Both/Custom/Online filter and the theme
/// grid. Tapping a tile applies its theme live; custom tiles carry Edit, and a long press offers Edit and Delete.
/// </summary>
public sealed class ApplicationThemeGalleryPageFragment : SettingsPageFragment<ApplicationThemeGalleryPageViewModel>
{
    private readonly List<(AppThemeBase Theme, ThemeTileView Tile)> _tiles = [];
    private ThemeGridLayout? _grid;
    private bool _reloadPending;

    protected override void BuildPage()
    {
        var vm = VM;

        var storageError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        storageError.ActionTitle = Translator.Buttons_Retry;
        Hook(storageError, () => vm.LoadThemesCommand.Execute(null), null);
        Bind.Bind(vm, nameof(vm.ErrorMessage), s => s.ErrorMessage, message => storageError.Message = message);
        Bind.Visible(storageError, vm, nameof(vm.IsStorageError), s => s.IsStorageError);
        Add(storageError);

        var applyError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        applyError.IsClosable = true;
        applyError.ActionTitle = Translator.Buttons_Retry;
        Hook(applyError, () => vm.RetryApplyCommand.Execute(null), () => vm.IsApplyError = false);
        Bind.Bind(vm, nameof(vm.ErrorMessage), s => s.ErrorMessage, message => applyError.Message = message);
        Bind.Visible(applyError, vm, nameof(vm.IsApplyError), s => s.IsApplyError);
        Add(applyError);

        // Current theme, with Create custom theme under it.
        var preview = new ThemeThumbnailView(Ctx, Resource.Dimension.wino_theme_thumb_large_width, Resource.Dimension.wino_theme_thumb_large_height);
        var name = Text(string.Empty, WinoTextStyle.BodyStrong);
        var compatibility = Caption(string.Empty);
        // The current theme is a list item (its preview, name and compatibility); Create custom theme is an action row.
        var currentCard = Card(string.Empty, null);
        currentCard.LeadingView = preview;
        var create = ActionCard(Translator.ApplicationThemeGallery_Create, vm.CreateThemeCommand);
        Bind.Visible(create, vm, nameof(vm.IsCreateThemeVisible), s => s.IsCreateThemeVisible);
        AddGroup(Translator.ApplicationThemeGallery_CurrentTheme, currentCard, create);
        Bind.Bind(vm, nameof(vm.CurrentTheme), s => s.CurrentTheme, theme =>
        {
            preview.Show(theme);
            currentCard.Header = theme?.ThemeName ?? string.Empty;
            currentCard.Description = theme is { IsCustomTheme: false } ? theme.CompatibilityTitle : null;
            SyncSelection();
        });

        // Filter (scrolls sideways on a phone).
        var filter = Bind.Segmented(vm,
            [Translator.ApplicationThemeGallery_All, Translator.ApplicationThemeGallery_Dark, Translator.ApplicationThemeGallery_Light,
             Translator.ApplicationThemeGallery_Both, Translator.ApplicationThemeGallery_Custom, Translator.ApplicationThemeGallery_Online],
            nameof(vm.SelectedFilterIndex), s => s.SelectedFilterIndex, (s, value) => s.SelectedFilterIndex = value, fillWidth: false);
        filter.ContentDescription = Translator.ApplicationThemeGallery_Title;
        var filterScroll = new HorizontalScrollView(Ctx) { HorizontalScrollBarEnabled = false };
        filterScroll.AddView(filter);
        Add(filterScroll);

        var spinner = new CircularProgressIndicator(Ctx) { Indeterminate = true, IndicatorSize = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_xl) };
        var loading = Row(spinner, Caption(Translator.ApplicationThemeGallery_Loading));
        loading.SetGravity(GravityFlags.Center);
        Bind.Visible(loading, vm, nameof(vm.IsLoading), s => s.IsLoading);
        Add(loading);

        var empty = Text(Translator.ApplicationThemeGallery_Empty, WinoTextStyle.BodyLarge);
        empty.Gravity = GravityFlags.Center;
        Bind.Visible(empty, vm, nameof(vm.IsEmpty), s => s.IsEmpty);
        Add(empty);

        var comingSoon = Text(Translator.ApplicationThemeGallery_ComingSoon, WinoTextStyle.BodyLargeStrong);
        comingSoon.Gravity = GravityFlags.Center;
        var onlineDescription = Text(Translator.ApplicationThemeGallery_OnlineDescription, WinoTextStyle.BodySecondary);
        onlineDescription.Gravity = GravityFlags.Center;
        var online = Stack(Resource.Dimension.wino_space_2xs, comingSoon, onlineDescription);
        Bind.Visible(online, vm, nameof(vm.IsOnline), s => s.IsOnline);
        Add(online);

        _grid = new ThemeGridLayout(Ctx);
        _grid.SetPadding(0, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s), 0, 0);
        Bind.Visible(_grid, vm, nameof(vm.IsLocalGalleryVisible), s => s.IsLocalGalleryVisible);
        Add(_grid);

        Bind.Collection(vm.FilteredThemes, Reload);
        Bind.Bind(vm, nameof(vm.IsApplying), s => s.IsApplying, applying =>
        {
            if (!applying) SyncSelection();
        });
    }

    /// <summary>The filter clears and re-adds every theme, one collection change each: rebuild once per burst.</summary>
    private void Reload()
    {
        if (_reloadPending || View is not { } view) return;
        _reloadPending = true;
        view.Post(() =>
        {
            _reloadPending = false;
            if (Bindings.IsDisposed) return;
            RebuildTiles();
            SyncSelection();
        });
    }

    private void RebuildTiles()
    {
        if (_grid is null) return;
        _grid.RemoveAllViews();
        _tiles.Clear();
        foreach (var theme in VM.FilteredThemes.ToList())
        {
            var tile = new ThemeTileView(Ctx, theme, theme.IsCustomTheme ? null : theme.CompatibilityTitle);
            tile.Picture.Click += (_, _) => Apply(theme);
            if (theme.IsCustomTheme)
            {
                var edit = SettingsBinder.CreateButton(Ctx, Translator.Buttons_Edit, icon: WinoIconGlyph.Edit);
                edit.Click += (_, _) => EditTheme(theme);
                tile.AddView(edit, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs) });
                tile.Picture.LongClick += (_, _) =>
                {
                    var menu = new PopupMenu(Ctx, tile.Picture);
                    menu.Menu.Add(0, 1, 0, Translator.Buttons_Edit);
                    menu.Menu.Add(0, 2, 1, Translator.Buttons_Delete);
                    menu.MenuItemClick += (_, args) =>
                    {
                        if (args.Item?.ItemId == 1) EditTheme(theme);
                        else RemoveTheme(theme);
                    };
                    menu.Show();
                };
            }
            _grid.AddView(tile);
            _tiles.Add((theme, tile));
        }
    }

    /// <summary>Marks the tile of the theme in use.</summary>
    private void SyncSelection()
    {
        var current = VM.CurrentTheme?.Id;
        foreach (var (theme, tile) in _tiles) tile.IsSelected = current is { } id && theme.Id == id;
    }

    private void Apply(AppThemeBase theme)
    {
        // While a theme is being applied the selection stays where it is.
        if (VM.IsApplying || theme.Id == VM.CurrentTheme?.Id) return;
        if (VM.ApplyThemeCommand.CanExecute(theme)) VM.ApplyThemeCommand.Execute(theme);
        SyncSelection();
    }

    private void EditTheme(AppThemeBase theme)
    {
        if (VM.EditThemeCommand.CanExecute(theme)) VM.EditThemeCommand.Execute(theme);
    }

    private void RemoveTheme(AppThemeBase theme)
    {
        if (theme.IsCustomTheme && VM.RemoveThemeCommand.CanExecute(theme)) VM.RemoveThemeCommand.Execute(theme);
    }

    private void Hook(WinoInfoBar bar, Action action, Action? closed)
    {
        EventHandler actionHandler = (_, _) => action();
        bar.ActionInvoked += actionHandler;
        EventHandler<WinoInfoBarClosedEventArgs> closedHandler = (_, _) => closed?.Invoke();
        bar.Closed += closedHandler;
        Bindings.Own(new ActionDisposable(() => { bar.ActionInvoked -= actionHandler; bar.Closed -= closedHandler; }));
    }
}
