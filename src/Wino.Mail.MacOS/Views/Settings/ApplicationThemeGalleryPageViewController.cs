using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Windows ApplicationThemeGalleryPage: storage and apply error bars with Retry, the current theme with
/// "Create custom theme", the All/Dark/Light/Both/Custom/Online filter and the theme grid. The grid is an
/// NSCollectionView whose single selection is the applied theme: selecting a tile applies it, arrow keys
/// and VoiceOver come from the collection view. Custom tiles carry an Edit button and an Edit/Delete
/// context menu; Delete (⌫) on a selected custom tile asks before removing it.
/// </summary>
public sealed class ApplicationThemeGalleryPageViewController(ApplicationThemeGalleryPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ApplicationThemeGalleryPageViewModel>(viewModel, dispatcher, logger)
{
    private const string ItemIdentifier = "WinoThemeGalleryItem";
    private static readonly CGSize ItemSize = new(WinoThemeTile.TileWidth + 10, 168);

    private ThemeCollectionView _grid = null!;
    private NSScrollView _gridScroll = null!;
    private NSLayoutConstraint _gridHeight = null!;
    private GalleryDataSource _source = null!;
    private GalleryDelegate _delegate = null!;
    private ThemeThumbnailView _currentPreview = null!;
    private NSTextField _currentName = null!;
    private NSTextField _currentCompatibility = null!;
    private bool _syncingSelection;

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

        // Current theme with Create custom theme.
        _currentPreview = new ThemeThumbnailView(72, 48);
        _currentName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        _currentCompatibility = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        var currentText = WinoLayout.VStack(2, _currentName, _currentCompatibility);
        currentText.Alignment = NSLayoutAttribute.Leading;
        var create = Bind.Button(Translator.ApplicationThemeGallery_Create, vm.CreateThemeCommand);
        Bind.Visible(create, vm, nameof(vm.IsCreateThemeVisible), s => s.IsCreateThemeVisible);
        var currentRow = WinoLayout.HStack(WinoStyle.Space3, _currentPreview, currentText, WinoLayout.Spacer(), create);
        currentRow.Alignment = NSLayoutAttribute.CenterY;
        var currentCard = Card(Translator.ApplicationThemeGallery_CurrentTheme, null);
        currentCard.Content = currentRow;
        AddGroup(null, currentCard);
        Bind.Bind(vm, nameof(vm.CurrentTheme), s => s.CurrentTheme, theme =>
        {
            _currentPreview.Show(theme);
            _currentName.StringValue = theme?.ThemeName ?? string.Empty;
            _currentCompatibility.StringValue = theme is { IsCustomTheme: false } ? theme.CompatibilityTitle : string.Empty;
            _currentCompatibility.Hidden = theme is null || theme.IsCustomTheme;
            SyncSelection();
        });

        // Filter.
        var filter = Bind.Segmented(vm,
            [Translator.ApplicationThemeGallery_All, Translator.ApplicationThemeGallery_Dark, Translator.ApplicationThemeGallery_Light,
             Translator.ApplicationThemeGallery_Both, Translator.ApplicationThemeGallery_Custom, Translator.ApplicationThemeGallery_Online],
            nameof(vm.SelectedFilterIndex), s => s.SelectedFilterIndex, (s, value) => s.SelectedFilterIndex = value);
        filter.ControlSize = NSControlSize.Regular;
        WinoAccessibility.Label(filter, Translator.ApplicationThemeGallery_Title);
        Add(filter);

        // Loading, empty and online states.
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true };
        spinner.StartAnimation(null);
        var loading = Centered(WinoLayout.HStack(WinoStyle.Space2, spinner, WinoStyle.Label(Translator.ApplicationThemeGallery_Loading, WinoStyle.Body, WinoStyle.SecondaryText)));
        Bind.Visible(loading, vm, nameof(vm.IsLoading), s => s.IsLoading);
        Add(loading);

        var empty = Centered(WinoStyle.Label(Translator.ApplicationThemeGallery_Empty, WinoStyle.Heading, WinoStyle.SecondaryText));
        Bind.Visible(empty, vm, nameof(vm.IsEmpty), s => s.IsEmpty);
        Add(empty);

        var onlineDescription = WinoStyle.Label(Translator.ApplicationThemeGallery_OnlineDescription, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        onlineDescription.Alignment = NSTextAlignment.Center;
        var online = WinoLayout.VStack(WinoStyle.Space1, WinoStyle.Label(Translator.ApplicationThemeGallery_ComingSoon, WinoStyle.Heading), onlineDescription);
        online.Alignment = NSLayoutAttribute.CenterX;
        Bind.Visible(online, vm, nameof(vm.IsOnline), s => s.IsOnline);
        Add(Centered(online));

        // The grid.
        _source = new GalleryDataSource(this);
        _delegate = new GalleryDelegate(this);
        var layout = new NSCollectionViewFlowLayout
        {
            ItemSize = ItemSize,
            MinimumInteritemSpacing = 12,
            MinimumLineSpacing = 16,
            SectionInset = new NSEdgeInsets(4, 0, 4, 0)
        };
        _grid = new ThemeCollectionView
        {
            CollectionViewLayout = layout,
            Selectable = true,
            AllowsEmptySelection = false,
            AllowsMultipleSelection = false,
            BackgroundColors = [NSColor.Clear],
            DataSource = _source,
            Delegate = _delegate,
            DeleteRequested = () => RemoveTheme(SelectedTheme())
        };
        _grid.RegisterClassForItem(typeof(ThemeGalleryItem), ItemIdentifier);
        WinoAccessibility.Label(_grid, Translator.ApplicationThemeGallery_Title);
        _gridScroll = new PassThroughScrollView
        {
            DocumentView = _grid,
            HasVerticalScroller = false,
            HasHorizontalScroller = false,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _grid.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _gridHeight = _gridScroll.HeightAnchor.ConstraintEqualTo(ItemSize.Height + 8);
        _gridHeight.Active = true;
        Bind.Visible(_gridScroll, vm, nameof(vm.IsLocalGalleryVisible), s => s.IsLocalGalleryVisible);
        Add(_gridScroll);

        Bind.Collection(vm.FilteredThemes, Reload);
        Bind.Bind(vm, nameof(vm.IsApplying), s => s.IsApplying, applying =>
        {
            // A finished apply (or a failed one) leaves the selection on the theme in use.
            if (!applying) SyncSelection();
        });
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);

    protected override Task DeactivateAsync()
    {
        _grid.DeleteRequested = null;
        return base.DeactivateAsync();
    }

    public override void ViewDidLayout()
    {
        base.ViewDidLayout();
        UpdateGridHeight();
    }

    private void Reload()
    {
        _grid.ReloadData();
        UpdateGridHeight();
        SyncSelection();
    }

    /// <summary>The grid is as tall as its content; the settings page scrolls, not the grid.</summary>
    private void UpdateGridHeight()
    {
        if (_gridScroll.Frame.Width <= 0) return;
        _grid.SetFrameSize(new CGSize(_gridScroll.ContentSize.Width, _grid.Frame.Height));
        _grid.CollectionViewLayout?.InvalidateLayout();
        _grid.LayoutSubtreeIfNeeded();
        var height = Math.Max(ItemSize.Height + 8, _grid.CollectionViewLayout?.CollectionViewContentSize.Height ?? 0);
        if (Math.Abs(_gridHeight.Constant - height) > 0.5) _gridHeight.Constant = (nfloat)height;
    }

    /// <summary>Selects the tile of the theme in use without applying it again.</summary>
    private void SyncSelection()
    {
        if (_grid is null) return;
        _syncingSelection = true;
        try
        {
            var current = ViewModel.CurrentTheme?.Id;
            var index = current is { } id ? ViewModel.FilteredThemes.ToList().FindIndex(theme => theme.Id == id) : -1;
            _grid.SelectionIndexPaths = index >= 0 ? new NSSet<NSIndexPath>(NSIndexPath.FromItemSection(index, 0)) : new NSSet<NSIndexPath>();
        }
        finally { _syncingSelection = false; }
    }

    private AppThemeBase? ThemeAt(nint index)
        => index >= 0 && index < ViewModel.FilteredThemes.Count ? ViewModel.FilteredThemes[(int)index] : null;

    private AppThemeBase? SelectedTheme()
        => _grid.SelectionIndexPaths.ToArray<NSIndexPath>().FirstOrDefault() is { } path ? ThemeAt(path.Item) : null;

    private void Apply(AppThemeBase? theme)
    {
        if (theme is null || theme.Id == ViewModel.CurrentTheme?.Id) return;
        if (ViewModel.ApplyThemeCommand.CanExecute(theme)) ViewModel.ApplyThemeCommand.Execute(theme);
        else SyncSelection();
    }

    private void EditTheme(AppThemeBase theme)
    {
        if (ViewModel.EditThemeCommand.CanExecute(theme)) ViewModel.EditThemeCommand.Execute(theme);
    }

    private void RemoveTheme(AppThemeBase? theme)
    {
        if (theme is { IsCustomTheme: true } && ViewModel.RemoveThemeCommand.CanExecute(theme)) ViewModel.RemoveThemeCommand.Execute(theme);
    }

    private NSMenu? MenuFor(AppThemeBase theme)
    {
        if (!theme.IsCustomTheme) return null;
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Buttons_Edit, (_, _) => EditTheme(theme)) { Image = WinoIcons.Image(WinoIconGlyph.Edit, 14) });
        menu.AddItem(NSMenuItem.SeparatorItem);
        var delete = new NSMenuItem(Translator.Buttons_Delete, (_, _) => RemoveTheme(theme))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Delete, 14),
            // The grid handles ⌫ on a selected custom tile (ThemeCollectionView.KeyDown).
            KeyEquivalent = "\b",
            KeyEquivalentModifierMask = 0
        };
        delete.AttributedTitle = new NSAttributedString(Translator.Buttons_Delete, new NSStringAttributes { ForegroundColor = NSColor.SystemRed, Font = NSFont.MenuFontOfSize(0) });
        menu.AddItem(delete);
        return menu;
    }

    private void Hook(WinoInfoBar bar, Action action, Action? closed)
    {
        EventHandler actionHandler = (_, _) => action();
        bar.ActionInvoked += actionHandler;
        EventHandler<WinoInfoBarClosedEventArgs> closedHandler = (_, _) => closed?.Invoke();
        bar.Closed += closedHandler;
        Bindings.Own(new ActionDisposable(() => { bar.ActionInvoked -= actionHandler; bar.Closed -= closedHandler; }));
    }

    private static NSStackView Centered(NSView view)
    {
        var row = WinoLayout.HStack(0, WinoLayout.Spacer(), view, WinoLayout.Spacer());
        row.Alignment = NSLayoutAttribute.CenterY;
        return row;
    }

    private sealed class GalleryDataSource(ApplicationThemeGalleryPageViewController owner) : NSCollectionViewDataSource
    {
        // The binding names the required collectionView:numberOfItemsInSection: GetNumberofItems and marks it obsolete in favour of a non-virtual twin.
        [Obsolete("Overrides the binding's required data source method.")]
        public override nint GetNumberofItems(NSCollectionView collectionView, nint section) => owner.ViewModel.FilteredThemes.Count;

        public override NSCollectionViewItem GetItem(NSCollectionView collectionView, NSIndexPath indexPath)
        {
            var item = (ThemeGalleryItem)collectionView.MakeItem(ItemIdentifier, indexPath);
            if (owner.ThemeAt(indexPath.Item) is { } theme)
                item.Configure(theme, () => owner.EditTheme(theme), () => owner.RemoveTheme(theme), owner.MenuFor(theme));
            return item;
        }
    }

    private sealed class GalleryDelegate(ApplicationThemeGalleryPageViewController owner) : NSCollectionViewDelegate
    {
        public override NSSet ShouldSelectItems(NSCollectionView collectionView, NSSet indexPaths)
            // While a theme is being applied the selection stays where it is.
            => owner.ViewModel.IsApplying && !owner._syncingSelection ? new NSSet() : indexPaths;

        public override void ItemsSelected(NSCollectionView collectionView, NSSet indexPaths)
        {
            if (owner._syncingSelection) return;
            if (indexPaths.ToArray<NSIndexPath>().FirstOrDefault() is { } path) owner.Apply(owner.ThemeAt(path.Item));
        }
    }

    /// <summary>Forwards Delete and Backspace on a selected custom tile to the gallery's delete path.</summary>
    private sealed class ThemeCollectionView : NSCollectionView
    {
        public Action? DeleteRequested { get; set; }

        public override void KeyDown(NSEvent theEvent)
        {
            // 51 is Delete (backspace), 117 is Forward Delete.
            if (theEvent.KeyCode is 51 or 117 && DeleteRequested is { } delete)
            {
                delete();
                return;
            }
            base.KeyDown(theEvent);
        }
    }

    /// <summary>The grid never scrolls on its own: wheel events go to the settings page.</summary>
    private sealed class PassThroughScrollView : NSScrollView
    {
        public override void ScrollWheel(NSEvent theEvent) => NextResponder?.ScrollWheel(theEvent);
    }
}

/// <summary>
/// One theme in the gallery grid: the existing <see cref="WinoThemeTile"/> (preview and name), the
/// light/dark compatibility for Wino themes and an Edit button for custom themes.
/// </summary>
[Register("WinoThemeGalleryItem")]
internal sealed class ThemeGalleryItem : NSCollectionViewItem
{
    private readonly NSStackView _stack = WinoLayout.VStack(4);
    private readonly NSTextField _compatibility = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
    private readonly NSButton _edit = SettingsBinder.CreateButton(Translator.Buttons_Edit);
    private WinoThemeTile? _tile;
    private Action? _editAction;

    public ThemeGalleryItem() { }

    public ThemeGalleryItem(NativeHandle handle) : base(handle) { }

    public override void LoadView()
    {
        var root = new NSView();
        _stack.Alignment = NSLayoutAttribute.Leading;
        _edit.ControlSize = NSControlSize.Small;
        _edit.Activated += (_, _) => _editAction?.Invoke();
        WinoLayout.Fill(_stack, root, 0, 4, 0, 4);
        View = root;
    }

    public void Configure(AppThemeBase theme, Action edit, Action delete, NSMenu? menu)
    {
        _ = View;
        _editAction = edit;
        if (_tile is not null) { _stack.RemoveArrangedSubview(_tile); _tile.RemoveFromSuperview(); _tile.Dispose(); }
        _stack.RemoveArrangedSubview(_compatibility); _compatibility.RemoveFromSuperview();
        _stack.RemoveArrangedSubview(_edit); _edit.RemoveFromSuperview();

        var gradient = theme.IsCustomTheme ? null : MacWinoThemeService.GradientFor(theme.ThemeName);
        _tile = new WinoThemeTile(theme.ThemeName ?? string.Empty, gradient is null ? ThemeThumbnailView.ImageFor(theme) : null, gradient,
            theme.ForceElementTheme == ApplicationElementTheme.Dark) { IsSelected = Selected };
        _stack.AddArrangedSubview(_tile);
        if (theme.IsCustomTheme) _stack.AddArrangedSubview(_edit);
        else
        {
            _compatibility.StringValue = theme.CompatibilityTitle;
            _stack.AddArrangedSubview(_compatibility);
        }

        View.Menu = menu;
        View.AccessibilityCustomActions = theme.IsCustomTheme
            ? [new NSAccessibilityCustomAction(Translator.Buttons_Edit, () => { edit(); return true; }),
               new NSAccessibilityCustomAction(Translator.Buttons_Delete, () => { delete(); return true; })]
            : [];
    }

    public override bool Selected
    {
        get => base.Selected;
        set
        {
            base.Selected = value;
            if (_tile is not null) _tile.IsSelected = value;
        }
    }
}
