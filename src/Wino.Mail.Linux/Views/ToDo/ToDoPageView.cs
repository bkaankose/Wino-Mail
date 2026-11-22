using System.Runtime.CompilerServices;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// To Do mode content (Windows ToDoPage, macOS ToDoPageViewController): the task surface (header, filter row, task list,
/// quick add) and the 340 px drawer at the trailing edge, laid out by <see cref="EndPaneLayout"/> on the page's own
/// width, with the blocked state over both. The page is a frame of hosts; the list, the filters and the drawer are
/// partial files that implement its hooks. The ViewModel outlives the page, so every subscription is owned by
/// <c>Bindings</c>.
/// </summary>
internal sealed partial class ToDoPageView : WinoView<ToDoPageViewModel>
{
    private const int DrawerWidth = 340;
    private const int WideMinimum = 728;

    private readonly StrongBox<ToDoPageView?> _self;
    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly EndPaneLayout _layout = new(DrawerWidth, WideMinimum, WideMinimum - 1);
    private readonly ToDoListHeader _header = new();
    private readonly Adw.Bin _listHost = Adw.Bin.New();
    private readonly Adw.Bin _quickAddHost = Adw.Bin.New();
    private readonly Adw.Bin _drawerHost = Adw.Bin.New();
    private readonly Gtk.Box _empty = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly WinoIcon _emptyIcon = new(WinoIconGlyph.CheckmarkCircle, 48);
    private readonly Gtk.Label _emptyTitle = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _emptyBody = Gtk.Label.New(string.Empty);
    private readonly ModeReadinessPanel _readiness;

    private readonly INotificationBuilder _notifications;

    public ToDoPageView(ToDoPageViewModel viewModel, WinoViewServices services, INotificationBuilder notifications)
        : base(viewModel, services)
    {
        _notifications = notifications;
        _self = new StrongBox<ToDoPageView?>(this);
        Bindings.Own(new ActionDisposable(() => _self.Value = null));
        Bindings.Own(_header);
        Bindings.Own(_layout);
        _readiness = Bindings.Own(new ModeReadinessPanel(ViewModel.Readiness, WinoIconGlyph.AppModeToDo, Dispatcher, ReportError));

        _listHost.SetVexpand(true);
        _listHost.SetHexpand(true);
        var listArea = Gtk.Overlay.New();
        listArea.SetVexpand(true);
        listArea.SetChild(_listHost);
        listArea.AddOverlay(BuildEmpty());
        listArea.AddOverlay(BuildFlaggedMailSurface());

        _quickAddHost.SetMarginTop(4);
        _quickAddHost.SetMarginStart(20);
        _quickAddHost.SetMarginEnd(20);
        _quickAddHost.SetMarginBottom(20);
        _quickAddHost.SetVisible(false);

        var surface = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        surface.Append(_header.Root);
        surface.Append(listArea);
        surface.Append(BuildBars());
        _barsHome = surface;
        _barsAfter = listArea;
        surface.Append(_quickAddHost);

        _drawerHost.AddCssClass(WinoCssClass.ToDoDrawer);
        _drawerHost.SetVexpand(true);
        _layout.Start = surface;
        _layout.End = _drawerHost;
        _root.SetChild(_layout.Root);
        _root.AddOverlay(_readiness.Root);

        var self = _self;
        Action stateChanged = () => self.Value?.OnLayoutStateChanged();
        _layout.StateChanged += stateChanged;
        Bindings.Own(new ActionDisposable(() => _layout.StateChanged -= stateChanged));
        _header.RenameRequested += () => self.Value?.RenameSelectedList();
        _header.OptionsRequested += anchor => self.Value?.ShowListOptions(anchor);

        BuildFilters(_header);
        BuildList(_listHost, _quickAddHost);
        BuildReorder();
        BuildDrawer(_drawerHost);
    }

    public override Gtk.Widget Root => _root;

    internal ToDoListHeader Header => _header;

    internal EndPaneLayout Layout => _layout;

    internal bool IsBlocked => _readiness.IsShown;

    internal bool IsEmptyStateShown => _empty.GetVisible();

    internal string EmptyStateGlyphName { get; private set; } = nameof(WinoIconGlyph.CheckmarkCircle);

    partial void BuildList(Adw.Bin listHost, Adw.Bin quickAddHost);
    partial void BindList();
    partial void BuildFilters(ToDoListHeader header);
    partial void BindFilters();
    partial void BuildDrawer(Adw.Bin drawerHost);
    partial void BuildReorder();
    partial void LayoutStateChanged(EndPaneState state);

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        DebugCurrent = new WeakReference<ToDoPageView>(this);
#endif
        Bind.Bind(nameof(ToDoPageViewModel.SelectedSurfaceTitle), static vm => vm.SelectedSurfaceTitle, _header.SetTitle);
        Bind.BindAny(static vm => (vm.SelectedSurfaceSubtitle, vm.HasSurfaceSubtitle), value => _header.SetSubtitle(value.SelectedSurfaceSubtitle, value.HasSurfaceSubtitle));
        Bind.Bind(nameof(ToDoPageViewModel.CanRenameSelectedList), static vm => vm.CanRenameSelectedList, _header.SetRenameVisible);
        Bind.Bind(nameof(ToDoPageViewModel.IsNamedListSelected), static vm => vm.IsNamedListSelected, _header.SetOptionsVisible);
        Bind.Bind(nameof(ToDoPageViewModel.IsDetailSurfaceVisible), static vm => vm.IsDetailSurfaceVisible, shows =>
        {
            _layout.ShowsEnd = shows;
            PlaceBars();
        });
        Bind.Bind(nameof(ToDoPageViewModel.IsEmpty), static vm => vm.IsEmpty, _ => ApplyEmptyState());
        Bind.Bind(nameof(ToDoPageViewModel.IsLoading), static vm => vm.IsLoading, _ => ApplyEmptyState());
        Bind.Bind(nameof(ToDoPageViewModel.EmptyStateTitle), static vm => vm.EmptyStateTitle, _ => ApplyEmptyState());
        Bind.Bind(nameof(ToDoPageViewModel.EmptyStateBody), static vm => vm.EmptyStateBody, _ => ApplyEmptyState());
        Bind.Bind(nameof(ToDoPageViewModel.SelectedView), static vm => vm.SelectedView, _ => ApplyEmptyState());
        Bind.Bind(nameof(ToDoPageViewModel.SelectedList), static vm => vm.SelectedList, _ => ApplyEmptyState());
        Bind.Bind(nameof(ToDoPageViewModel.IsQuickAddVisible), static vm => vm.IsQuickAddVisible, visible => _quickAddHost.SetVisible(visible && _quickAddHost.GetChild() is not null));
        OnLayoutStateChanged();
        BindBars();
        BindFlaggedMail();
        BindFilters();
        BindList();
        return ViewModel.InitializeNavigationAsync(mode, parameter);
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (DebugCurrent is not null && DebugCurrent.TryGetTarget(out var current) && ReferenceEquals(current, this)) DebugCurrent = null;
#endif
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        return base.DeactivateAsync();
    }

#if DEBUG
    internal static WeakReference<ToDoPageView>? DebugCurrent { get; private set; }
#endif

    private void OnLayoutStateChanged()
    {
        if (IsReleased) return;
        ViewModel.SetCompactLayout(_layout.State == EndPaneState.Swapped);
        LayoutStateChanged(_layout.State);
        PlaceBars();
    }

    private void RenameSelectedList()
    {
        if (IsReleased || !ViewModel.CanRenameSelectedList) return;
        Observe(ViewModel.RenameSelectedListCommand.ExecuteAsync(null));
    }

    private void ShowListOptions(Gtk.Widget anchor)
    {
        if (IsReleased || !ViewModel.IsNamedListSelected) return;
        ContextMenu.Show(anchor, BuildListOptions().Build());
    }

    /// <summary>
    /// Windows list options flyout: rename, copy to another list, export, import, print, e-mail, and delete when the list
    /// allows it.
    /// </summary>
    internal WinoMenuBuilder BuildListOptions()
    {
        var menu = WinoMenus.Create();
        var self = _self;
        menu.Item(Translator.ToDoPage_RenameList, () => self.Value?.RenameSelectedList(), ViewModel.CanRenameSelectedList, icon: WinoIconGlyph.Rename);
        menu.Separator();
        menu.Item(Translator.ToDoPage_CopyListTo, () =>
        {
            if (self.Value is { } view) view.Observe(view.ViewModel.CopySelectedListCommand.ExecuteAsync(null));
        }, icon: WinoIconGlyph.Copy);
        menu.Submenu(Translator.ToDoPage_ExportTasks, export =>
        {
            export.Item(Translator.ToDoPage_ExportIcs, () => self.Value?.RunExport("ics"));
            export.Item(Translator.ToDoPage_ExportCsv, () => self.Value?.RunExport("csv"));
        }, icon: WinoIconGlyph.ArrowUpload);
        menu.Item(Translator.ToDoPage_ImportTasks, () =>
        {
            if (self.Value is { } view) view.Observe(view.ViewModel.ImportTasksCommand.ExecuteAsync(null));
        }, icon: WinoIconGlyph.ArrowDownload);
        menu.Separator();
        menu.Item(Translator.ToDoPage_PrintList, () => self.Value?.ViewModel.PrintListCommand.Execute(null), icon: WinoIconGlyph.Print);
        menu.Item(Translator.ToDoPage_EmailList, () => self.Value?.ViewModel.EmailListCommand.Execute(null), icon: WinoIconGlyph.Mail);
        if (ViewModel.CanDeleteSelectedList)
        {
            menu.Separator();
            menu.Item(Translator.ToDoPage_DeleteList, () =>
            {
                if (self.Value is { } view) view.Observe(view.ViewModel.DeleteListCommand.ExecuteAsync(null));
            }, icon: WinoIconGlyph.Delete, destructive: true);
        }
        return menu;
    }

    private void RunExport(string format)
    {
        if (IsReleased) return;
        Observe(ViewModel.ExportTasksCommand.ExecuteAsync(format));
    }

    private Gtk.Widget BuildEmpty()
    {
        _empty.SetHalign(Gtk.Align.Center);
        _empty.SetValign(Gtk.Align.Center);
        _empty.SetCanTarget(false);
        _empty.SetMarginStart(24);
        _empty.SetMarginEnd(24);
        _empty.SetVisible(false);
        var icon = _emptyIcon.Root;
        icon.AddCssClass("dim-label");
        icon.SetMarginBottom(4);
        _empty.Append(icon);
        _emptyTitle.AddCssClass("heading");
        _emptyTitle.SetWrap(true);
        _emptyTitle.SetJustify(Gtk.Justification.Center);
        _emptyTitle.SetMaxWidthChars(40);
        _empty.Append(_emptyTitle);
        _emptyBody.AddCssClass("dim-label");
        _emptyBody.SetWrap(true);
        _emptyBody.SetJustify(Gtk.Justification.Center);
        _emptyBody.SetMaxWidthChars(40);
        _empty.Append(_emptyBody);
        return _empty;
    }

    private void ApplyEmptyState()
    {
        if (IsReleased) return;
        var glyph = EmptyStateGlyph();
        EmptyStateGlyphName = glyph.ToString();
        _emptyIcon.SetIcon(glyph);
        _emptyTitle.SetText(ViewModel.EmptyStateTitle ?? string.Empty);
        _emptyBody.SetText(ViewModel.EmptyStateBody ?? string.Empty);
        _emptyBody.SetVisible(!string.IsNullOrEmpty(ViewModel.EmptyStateBody));
        _empty.SetVisible(ViewModel.IsTaskEmptyStateVisible && !ViewModel.IsLoading);
    }

    private WinoIconGlyph EmptyStateGlyph()
    {
        if (ViewModel.IsEmptyAccountSelected || ViewModel.SelectedList is not null) return WinoIconGlyph.TaskList;
        return ViewModel.SelectedView switch
        {
            TaskViewKind.MyDay => WinoIconGlyph.WeatherSunny,
            TaskViewKind.Important => WinoIconGlyph.Star,
            _ => WinoIconGlyph.CheckmarkCircle,
        };
    }
}
