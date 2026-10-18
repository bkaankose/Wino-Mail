using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Windows ApplicationThemeGalleryPage: storage and apply error bars with Retry, the current theme with
/// "Create custom theme", the All/Dark/Light/Both/Custom/Online filter and the theme grid. The grid wraps
/// <see cref="WinoThemeTile"/>s to the sheet width; tapping a tile applies its theme. Custom tiles carry an Edit
/// button, an Edit/Delete context menu (long press) and the same two VoiceOver actions.
/// </summary>
public sealed class ApplicationThemeGalleryPageViewController(ApplicationThemeGalleryPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ApplicationThemeGalleryPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly ThemeGridView _grid = new();
    private readonly List<(AppThemeBase Theme, WinoThemeTile Tile)> _tiles = [];
    private readonly ThemeThumbnailView _currentPreview = new(72, 48);
    private readonly UILabel _currentName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
    private readonly UILabel _currentCompatibility = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
    private bool _reloadPending;
    private bool _deactivated;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // Error bars (Windows InfoBars): storage errors are not closable; apply errors are.
        var storageError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        storageError.IsClosable = false;
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

        // Current theme; Create custom theme is the navigation bar's "+" (18 Oct 2026).
        var currentText = WinoLayout.VStack(2, _currentName, _currentCompatibility);
        currentText.Alignment = UIStackViewAlignment.Leading;
        var currentRow = WinoLayout.HStack(WinoStyle.Space3, _currentPreview, currentText);
        currentRow.Alignment = UIStackViewAlignment.Center;
        var create = BarButton(WinoIconGlyph.Add, Translator.ApplicationThemeGallery_Create, vm.CreateThemeCommand);
        Bind.Bind(vm, nameof(vm.IsCreateThemeVisible), s => s.IsCreateThemeVisible, visible => SetBarItemVisible(create, visible));
        var currentContent = WinoLayout.VStack(WinoStyle.Space3, currentRow);
        currentContent.Alignment = UIStackViewAlignment.Leading;
        var currentCard = Card(Translator.ApplicationThemeGallery_CurrentTheme, null, WinoIconGlyph.PaintBrush);
        currentCard.BottomContent = currentContent;
        AddGroup(null, currentCard);
        Bind.Bind(vm, nameof(vm.CurrentTheme), s => s.CurrentTheme, theme =>
        {
            _currentPreview.Show(theme);
            _currentName.Text = theme?.ThemeName ?? string.Empty;
            _currentCompatibility.Text = theme is { IsCustomTheme: false } ? theme.CompatibilityTitle : string.Empty;
            _currentCompatibility.Hidden = theme is null || theme.IsCustomTheme;
            SyncSelection();
        });

        // Filter.
        var filter = Bind.Segmented(vm,
            [Translator.ApplicationThemeGallery_All, Translator.ApplicationThemeGallery_Dark, Translator.ApplicationThemeGallery_Light,
             Translator.ApplicationThemeGallery_Both, Translator.ApplicationThemeGallery_Custom, Translator.ApplicationThemeGallery_Online],
            nameof(vm.SelectedFilterIndex), s => s.SelectedFilterIndex, (s, value) => s.SelectedFilterIndex = value);
        filter.ApportionsSegmentWidthsByContent = true;
        WinoAccessibility.Label(filter, Translator.ApplicationThemeGallery_Title);
        Add(filter);

        // Loading, empty and online states.
        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { TranslatesAutoresizingMaskIntoConstraints = false };
        spinner.StartAnimating();
        var loading = Centered(WinoLayout.HStack(WinoStyle.Space2, spinner, WinoStyle.Label(Translator.ApplicationThemeGallery_Loading, WinoStyle.Body, WinoStyle.SecondaryText)));
        Bind.Visible(loading, vm, nameof(vm.IsLoading), s => s.IsLoading);
        Add(loading);

        var empty = Centered(WinoStyle.Label(Translator.ApplicationThemeGallery_Empty, WinoStyle.Heading, WinoStyle.SecondaryText, 0));
        Bind.Visible(empty, vm, nameof(vm.IsEmpty), s => s.IsEmpty);
        Add(empty);

        var onlineDescription = WinoStyle.Label(Translator.ApplicationThemeGallery_OnlineDescription, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        onlineDescription.TextAlignment = UITextAlignment.Center;
        var online = WinoLayout.VStack(WinoStyle.Space1, WinoStyle.Label(Translator.ApplicationThemeGallery_ComingSoon, WinoStyle.Heading), onlineDescription);
        online.Alignment = UIStackViewAlignment.Center;
        Bind.Visible(online, vm, nameof(vm.IsOnline), s => s.IsOnline);
        Add(online);

        // The grid.
        Bind.Visible(_grid, vm, nameof(vm.IsLocalGalleryVisible), s => s.IsLocalGalleryVisible);
        Add(_grid);

        Bind.Collection(vm.FilteredThemes, Reload);
        Bind.Bind(vm, nameof(vm.IsApplying), s => s.IsApplying, applying =>
        {
            // A finished apply (or a failed one) leaves the selection on the theme in use.
            if (!applying) SyncSelection();
        });
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter);

    protected override Task DeactivateAsync()
    {
        _deactivated = true;
        return base.DeactivateAsync();
    }

    /// <summary>The filter clears and re-adds every theme, one collection change each: rebuild once per burst.</summary>
    private void Reload()
    {
        if (_reloadPending) return;
        _reloadPending = true;
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _reloadPending = false;
            if (_deactivated) return;
            RebuildTiles();
            SyncSelection();
        });
    }

    private void RebuildTiles()
    {
        _tiles.Clear();
        var cells = new List<UIView>();
        foreach (var theme in ViewModel.FilteredThemes.ToList())
        {
            var gradient = theme.IsCustomTheme ? null : UIKitThemeService.GradientFor(theme.ThemeName);
            var tile = new WinoThemeTile(theme.ThemeName ?? string.Empty, gradient is null ? ThemeThumbnailView.ImageFor(theme) : null, gradient,
                theme.ForceElementTheme == ApplicationElementTheme.Dark);
            tile.Pressed += (_, _) => Apply(theme);

            var stack = WinoLayout.VStack(WinoStyle.Space1, tile);
            stack.Alignment = UIStackViewAlignment.Leading;
            if (theme.IsCustomTheme)
            {
                var edit = SettingsBinder.CreateButton(Translator.Buttons_Edit, icon: WinoIconGlyph.Edit);
                edit.PrimaryActionTriggered += (_, _) => EditTheme(theme);
                edit.HeightAnchor.ConstraintGreaterThanOrEqualTo(36).Active = true;
                stack.AddArrangedSubview(edit);
                tile.AccessibilityCustomActions =
                [
                    new UIAccessibilityCustomAction(Translator.Buttons_Edit, (Func<UIAccessibilityCustomAction, bool>)(_ => { EditTheme(theme); return true; })),
                    new UIAccessibilityCustomAction(Translator.Buttons_Delete, (Func<UIAccessibilityCustomAction, bool>)(_ => { RemoveTheme(theme); return true; }))
                ];
                tile.AddInteraction(new UIContextMenuInteraction(new ThemeMenuDelegate(() => EditTheme(theme), () => RemoveTheme(theme))));
            }
            else
            {
                stack.AddArrangedSubview(WinoStyle.Label(theme.CompatibilityTitle, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText));
            }

            var cell = new UIView();
            stack.TranslatesAutoresizingMaskIntoConstraints = false;
            cell.AddSubview(stack);
            NSLayoutConstraint.ActivateConstraints(
            [
                stack.TopAnchor.ConstraintEqualTo(cell.TopAnchor),
                stack.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
                stack.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor)
            ]);
            cells.Add(cell);
            _tiles.Add((theme, tile));
        }
        _grid.SetCells(cells);
    }

    /// <summary>Marks the tile of the theme in use.</summary>
    private void SyncSelection()
    {
        var current = ViewModel.CurrentTheme?.Id;
        foreach (var (theme, tile) in _tiles) tile.IsSelected = current is { } id && theme.Id == id;
    }

    private void Apply(AppThemeBase theme)
    {
        // While a theme is being applied the selection stays where it is.
        if (ViewModel.IsApplying || theme.Id == ViewModel.CurrentTheme?.Id) return;
        if (ViewModel.ApplyThemeCommand.CanExecute(theme)) ViewModel.ApplyThemeCommand.Execute(theme);
        SyncSelection();
    }

    private void EditTheme(AppThemeBase theme)
    {
        if (ViewModel.EditThemeCommand.CanExecute(theme)) ViewModel.EditThemeCommand.Execute(theme);
    }

    private void RemoveTheme(AppThemeBase theme)
    {
        if (theme.IsCustomTheme && ViewModel.RemoveThemeCommand.CanExecute(theme)) ViewModel.RemoveThemeCommand.Execute(theme);
    }

    private void Hook(WinoInfoBar bar, Action action, Action? closed)
    {
        EventHandler actionHandler = (_, _) => action();
        bar.ActionInvoked += actionHandler;
        EventHandler<WinoInfoBarClosedEventArgs> closedHandler = (_, _) => closed?.Invoke();
        bar.Closed += closedHandler;
        Bindings.Own(new ActionDisposable(() => { bar.ActionInvoked -= actionHandler; bar.Closed -= closedHandler; }));
    }

    private static UIStackView Centered(UIView view)
    {
        var row = WinoLayout.HStack(0, WinoLayout.Spacer(), view, WinoLayout.Spacer());
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>Edit and Delete on a long press of a custom theme tile.</summary>
    private sealed class ThemeMenuDelegate(Action edit, Action delete) : UIContextMenuInteractionDelegate
    {
        public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
            => UIContextMenuConfiguration.Create(null, null, _ =>
            {
                var editAction = UIAction.Create(Translator.Buttons_Edit, WinoIcons.Image(WinoIconGlyph.Edit, 17), null, _ => edit());
                var deleteAction = UIAction.Create(Translator.Buttons_Delete, WinoIcons.Image(WinoIconGlyph.Delete, 17), null, _ => delete());
                deleteAction.Attributes = UIMenuElementAttributes.Destructive;
                return UIMenu.Create([editAction, deleteAction]);
            });
    }
}

/// <summary>
/// A wrapping grid of fixed-size theme cells sized to its width: as many <see cref="WinoThemeTile.TileWidth"/> columns
/// as fit, spread evenly, and as tall as its rows (the settings page scrolls, not the grid).
/// </summary>
internal sealed class ThemeGridView : UIView
{
    private const double CellWidth = WinoThemeTile.TileWidth;
    private const double CellHeight = 160;
    private const double MinimumSpacing = 12;
    private const double LineSpacing = 16;
    private readonly List<UIView> _cells = [];
    private int _laidOutColumns = -1;

    public ThemeGridView() => TranslatesAutoresizingMaskIntoConstraints = false;

    public void SetCells(IEnumerable<UIView> cells)
    {
        foreach (var cell in _cells) cell.RemoveFromSuperview();
        _cells.Clear();
        foreach (var cell in cells)
        {
            cell.TranslatesAutoresizingMaskIntoConstraints = true;
            _cells.Add(cell);
            AddSubview(cell);
        }
        _laidOutColumns = -1;
        InvalidateIntrinsicContentSize();
        SetNeedsLayout();
    }

    private static int Columns(double width) => Math.Max(1, (int)Math.Floor((width + MinimumSpacing) / (CellWidth + MinimumSpacing)));

    public override CGSize IntrinsicContentSize
    {
        get
        {
            if (_cells.Count == 0) return new CGSize(NoIntrinsicMetric, 0);
            var columns = Columns(Bounds.Width > 0 ? Bounds.Width : CellWidth * 2 + MinimumSpacing);
            var rows = (int)Math.Ceiling(_cells.Count / (double)columns);
            return new CGSize(NoIntrinsicMetric, (nfloat)(rows * CellHeight + (rows - 1) * LineSpacing));
        }
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var width = (double)Bounds.Width;
        var columns = Columns(width);
        var spacing = columns > 1 ? Math.Max(MinimumSpacing, (width - columns * CellWidth) / (columns - 1)) : 0;
        for (int index = 0; index < _cells.Count; index++)
        {
            int row = index / columns, column = index % columns;
            _cells[index].Frame = new CGRect(column * (CellWidth + spacing), row * (CellHeight + LineSpacing), CellWidth, CellHeight);
        }
        if (columns != _laidOutColumns)
        {
            _laidOutColumns = columns;
            InvalidateIntrinsicContentSize();
        }
    }
}
