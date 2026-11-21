using Wino.Core.Domain;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The theme gallery (Windows ApplicationThemeGalleryPage): errors, the current theme with Create, the filter, and the
/// tiles as one radio group of toggle buttons; activating a tile applies its theme. Wallpaper previews decode one per
/// main-loop idle turn, so the page is usable before they finish.
/// </summary>
internal sealed class ApplicationThemeGalleryPage(ApplicationThemeGalleryPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ApplicationThemeGalleryPageViewModel>(viewModel, services)
{
    private readonly Adw.WrapBox _grid = Adw.WrapBox.New();
    private readonly List<(WinoThemeTile Tile, AppThemeBase Theme)> _tiles = [];
    private readonly Queue<(WinoThemeTile Tile, string Path)> _pending = [];
    private readonly Gtk.Box _currentPreview = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Picture _currentPicture = Gtk.Picture.New();
    private string? _currentGradient;
    private SettingsBinder? _tileBinder;
    private uint _loader;
    private bool _syncing;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var storageError = Page.AddBanner();
        storageError.SetButtonLabel(Translator.Buttons_Retry);
        Settings.Bind(vm, nameof(vm.ErrorMessage), static s => s.ErrorMessage, text => storageError.SetTitle(text ?? string.Empty));
        Settings.Bind(vm, nameof(vm.IsStorageError), static s => s.IsStorageError, storageError.SetRevealed);
        Settings.Own(GtkSignals.Connect<Adw.Banner>(h => storageError.OnButtonClicked += h, h => storageError.OnButtonClicked -= h,
            (_, _) => vm.LoadThemesCommand.Execute(null)));

        var applyError = Page.AddBanner();
        applyError.SetButtonLabel(Translator.Buttons_Retry);
        Settings.Bind(vm, nameof(vm.ErrorMessage), static s => s.ErrorMessage, text => applyError.SetTitle(text ?? string.Empty));
        Settings.Bind(vm, nameof(vm.IsApplyError), static s => s.IsApplyError, applyError.SetRevealed);
        Settings.Own(GtkSignals.Connect<Adw.Banner>(h => applyError.OnButtonClicked += h, h => applyError.OnButtonClicked -= h,
            (_, _) => vm.RetryApplyCommand.Execute(null)));

        WinoSwatchCss.EnsureInstalled();
        _currentPicture.SetContentFit(Gtk.ContentFit.Cover);
        _currentPicture.SetCanShrink(true);
        _currentPicture.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _currentPreview.AddCssClass("wino-theme-preview");
        _currentPreview.SetOverflow(Gtk.Overflow.Hidden);
        _currentPreview.SetSizeRequest(64, 40);
        _currentPreview.SetHexpand(false);
        _currentPreview.Append(WinoThemeTile.Clamped(_currentPicture, 64));
        var current = WinoSettingsRows.Action(Translator.ApplicationThemeGallery_CurrentTheme);
        Settings.Subtitle(current, vm, nameof(vm.CurrentTheme), static s => s.CurrentTheme?.ThemeName);
        WinoSettingsRows.Suffix(current, _currentPreview);
        WinoSettingsRows.Suffix(current, Settings.Visible(Settings.Button(Translator.ApplicationThemeGallery_Create, vm.CreateThemeCommand),
            vm, nameof(vm.IsCreateThemeVisible), static s => s.IsCreateThemeVisible));
        AddGroup(null, current);

        AddGroup(null, Settings.Choice(vm, Translator.ApplicationThemeGallery_Title, null, Wino.Core.Domain.Enums.WinoIconGlyph.None,
            [Translator.ApplicationThemeGallery_All, Translator.ApplicationThemeGallery_Dark, Translator.ApplicationThemeGallery_Light,
             Translator.ApplicationThemeGallery_Both, Translator.ApplicationThemeGallery_Custom, Translator.ApplicationThemeGallery_Online],
            nameof(vm.SelectedFilterIndex), static s => s.SelectedFilterIndex, static (s, v) => s.SelectedFilterIndex = v));

        var loading = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        loading.SetHalign(Gtk.Align.Center);
        loading.Append(Adw.Spinner.New());
        loading.Append(Gtk.Label.New(Translator.ApplicationThemeGallery_Loading));
        var empty = Gtk.Label.New(Translator.ApplicationThemeGallery_Empty);
        empty.AddCssClass("title-4");
        empty.AddCssClass("dim-label");
        var onlineTitle = Gtk.Label.New(Translator.ApplicationThemeGallery_ComingSoon);
        onlineTitle.AddCssClass("title-4");
        var onlineDescription = WinoSettingsRows.Caption(Translator.ApplicationThemeGallery_OnlineDescription);
        onlineDescription.SetXalign(0.5f);
        onlineDescription.SetJustify(Gtk.Justification.Center);
        var online = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        online.Append(onlineTitle);
        online.Append(onlineDescription);

        _grid.SetChildSpacing(12);
        _grid.SetLineSpacing(16);
        _grid.SetJustify(Adw.JustifyMode.None);
        WinoA11y.Label(_grid, Translator.ApplicationThemeGallery_Title);
        var grid = Settings.Enabled(_grid, vm, nameof(vm.IsApplying), static s => !s.IsApplying);

        var states = Page.AddGroup();
        states.AddRange(
            Settings.Visible(WinoSettingsRows.Custom(loading), vm, nameof(vm.IsLoading), static s => s.IsLoading),
            Settings.Visible(WinoSettingsRows.Custom(empty), vm, nameof(vm.IsEmpty), static s => s.IsEmpty),
            Settings.Visible(WinoSettingsRows.Custom(online), vm, nameof(vm.IsOnline), static s => s.IsOnline),
            Settings.Visible(WinoSettingsRows.Custom(grid, Translator.ApplicationThemeGallery_Title), vm, nameof(vm.IsLocalGalleryVisible), static s => s.IsLocalGalleryVisible));

        Settings.Collection(vm.FilteredThemes, Reload);
        Settings.Bind(vm, nameof(vm.CurrentTheme), static s => s.CurrentTheme, theme =>
        {
            ShowCurrent(theme);
            SyncSelection();
        });
        Settings.Bind(vm, nameof(vm.IsApplying), static s => s.IsApplying, applying =>
        {
            if (!applying) SyncSelection();
        });
        WinoAppearance.Changed.Add(this, static page => page.RunOnUI(page.AppearanceChanged));
        Settings.Own(new ActionDisposable(() => WinoAppearance.Changed.Remove(this)));
        Settings.Own(new ActionDisposable(StopLoader));
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);

    private void Reload()
    {
        StopLoader();
        _pending.Clear();
        _tileBinder?.Scope.Dispose();
        foreach (var (tile, _) in _tiles) _grid.Remove(tile.Container);
        _tiles.Clear();
        var scope = _tileBinder = Settings.Child();

        Gtk.ToggleButton? group = null;
        foreach (var theme in ViewModel.FilteredThemes.ToList())
        {
            var tile = new WinoThemeTile(ThemePreviews.Model(theme), Translator.Buttons_Edit);
            if (group is null) group = tile.Root;
            else tile.Root.SetGroup(group);
            scope.Own(GtkSignals.OnToggled(tile.Root, () =>
            {
                if (_syncing || !tile.Root.GetActive()) return;
                Apply(theme);
            }));
            if (tile.EditButton is { } edit)
            {
                scope.Own(GtkSignals.OnClicked(edit, () => ViewModel.EditThemeCommand.Execute(theme)));
                scope.Own(ContextMenu.Attach(tile.Root, menu => menu
                    .Item(Translator.Buttons_Edit, () => MainLoop.Post(() => ViewModel.EditThemeCommand.Execute(theme)))
                    .Item(Translator.Buttons_Delete, () => MainLoop.Post(() =>
                    {
                        if (ViewModel.RemoveThemeCommand.CanExecute(theme)) ViewModel.RemoveThemeCommand.Execute(theme);
                    }))));
            }
            if (tile.Model.ImagePath is { } path)
            {
                if (ThemePreviews.TryGet(path, out var cached)) tile.SetTexture(cached);
                else _pending.Enqueue((tile, path));
            }
            _tiles.Add((tile, theme));
            _grid.Append(tile.Container);
        }
        SyncSelection();
        StartLoader();
    }

    private void Apply(AppThemeBase theme)
    {
        if (theme.Id == ViewModel.CurrentTheme?.Id) return;
        if (ViewModel.ApplyThemeCommand.CanExecute(theme)) ViewModel.ApplyThemeCommand.Execute(theme);
        else SyncSelection();
    }

    /// <summary>Marks the tile of the theme in use without applying it again.</summary>
    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            var current = ViewModel.CurrentTheme?.Id;
            var any = false;
            foreach (var (tile, theme) in _tiles)
            {
                var active = current is { } id && theme.Id == id;
                any |= active;
                tile.Root.SetActive(active);
            }
            if (!any)
            {
                foreach (var (tile, _) in _tiles) tile.Root.SetActive(false);
            }
        }
        finally { _syncing = false; }
    }

    private void ShowCurrent(AppThemeBase? theme)
    {
        if (_currentGradient is not null) _currentPreview.RemoveCssClass(_currentGradient);
        _currentGradient = null;
        _currentPicture.SetPaintable(null);
        if (theme is null) return;
        var model = ThemePreviews.Model(theme);
        if (model.GradientStart is { } start && model.GradientEnd is { } end)
        {
            _currentGradient = WinoSwatchCss.Gradient(start, end);
            _currentPreview.AddCssClass(_currentGradient);
        }
        else if (model.ImagePath is { } path)
        {
            _currentPicture.SetPaintable(ThemePreviews.Load(path));
        }
    }

    private void AppearanceChanged()
    {
        foreach (var (tile, theme) in _tiles)
        {
            var model = ThemePreviews.Model(theme);
            if (model.GradientStart is { } start && model.GradientEnd is { } end) tile.SetGradient(start, end);
        }
        ShowCurrent(ViewModel.CurrentTheme);
    }

    private void StartLoader()
    {
        if (_loader != 0 || _pending.Count == 0) return;
        var started = DateTime.UtcNow;
        _loader = GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            if (IsReleased || !_pending.TryDequeue(out var next))
            {
                // Cleared before returning false: removing a finished source logs a GLib critical.
                _loader = 0;
                if (!IsReleased) Serilog.Log.Debug("Theme gallery previews decoded in {Elapsed} ms.", (int)(DateTime.UtcNow - started).TotalMilliseconds);
                return false;
            }
            next.Tile.SetTexture(ThemePreviews.Load(next.Path));
            return true;
        });
    }

    private void StopLoader()
    {
        if (_loader == 0) return;
        GLib.Functions.SourceRemove(_loader);
        _loader = 0;
    }
}
