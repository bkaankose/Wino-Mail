using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>
/// To Do mode content page (Windows ToDoPage): one Wino zone holding the task surface (header,
/// filter row, grouped task cards, quick add) and the 340pt detail drawer with a left stroke.
/// Below the Windows breakpoint (1008pt window width) the two surfaces swap instead of sharing.
/// Partial files: List (header, filters, table, quick add) and Detail (drawer, bulk selection).
/// </summary>
public sealed partial class ToDoPageViewController : WinoViewController<ToDoPageViewModel>
{
    private readonly WinoZoneView _zone = new();
    private NSView _listSurface = null!;
    private NSView _drawerHost = null!;
    private NSView _detailPane = null!;
    private NSView _multiPane = null!;
    private NSView _blockedPanel = null!;
    private NSLayoutConstraint _drawerWidth = null!;
    private NSLayoutConstraint _drawerFill = null!;
    private bool _released;
    private bool _compact;

    public ToDoPageViewController(ToDoPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
        : base(viewModel, dispatcher, logger)
    {
    }

    public override void LoadView()
    {
        var root = new ToDoPage();
        _zone.Fill = WinoStyle.ZoneFill;
        WinoLayout.Fill(_zone, root, 2, 4, 7, 7);
        var content = _zone.ContentView;

        _listSurface = BuildListSurface();
        _drawerHost = BuildDrawerHost();
        content.AddSubview(_listSurface);
        content.AddSubview(_drawerHost);

        _drawerWidth = _drawerHost.WidthAnchor.ConstraintEqualTo((nfloat)WinoToDoStyle.DrawerWidth);
        _drawerFill = _drawerHost.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor);
        NSLayoutConstraint.ActivateConstraints(
        [
            _listSurface.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _listSurface.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _listSurface.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            _listSurface.TrailingAnchor.ConstraintEqualTo(_drawerHost.LeadingAnchor),
            _drawerHost.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _drawerHost.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            _drawerHost.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _drawerWidth
        ]);

        _blockedPanel = BuildBlockedPanel();
        WinoLayout.Fill(_blockedPanel, content);
        View = root;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BindList();
        BindDetail();
        BindLayout();
        RegisterDebugCommands();
        ViewModel.OnNavigatedTo(mode, parameter!);
        return Task.CompletedTask;
    }

    protected override Task DeactivateAsync()
    {
        ReleaseList();
        ReleaseDetail();
        return base.DeactivateAsync();
    }

    public override async Task ReleaseAsync()
    {
        _released = true;
        await base.ReleaseAsync();
    }

    public override void ViewDidLayout()
    {
        base.ViewDidLayout();
        UpdateDetailTitleWidth();
        var width = View.Window?.Frame.Width ?? View.Bounds.Width;
        bool compact = width < WinoToDoStyle.CompactBreakpoint;
        if (compact == _compact) return;
        _compact = compact;
        ViewModel.SetCompactLayout(compact);
    }

    /// <summary>Surface visibility: list and drawer side by side, or one at a time when compact.</summary>
    private void BindLayout()
    {
        foreach (var property in new[] { nameof(ViewModel.IsTaskListSurfaceVisible), nameof(ViewModel.IsDetailSurfaceVisible), nameof(ViewModel.IsCompactLayout),
                     nameof(ViewModel.IsSingleTaskSelection), nameof(ViewModel.IsMultipleTaskSelection) })
            Bind(property, static _ => 0, _ => ApplyLayout());

        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsBlocked),
            static readiness => readiness.IsBlocked, blocked => { _blockedPanel.Hidden = !blocked; ApplyLayout(); }, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, string>(ViewModel.Readiness, nameof(ModeReadinessViewModel.Title),
            static readiness => readiness.Title, value => _blockedTitle.StringValue = value ?? string.Empty, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, string>(ViewModel.Readiness, nameof(ModeReadinessViewModel.Message),
            static readiness => readiness.Message, value => _blockedMessage.StringValue = value ?? string.Empty, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, string>(ViewModel.Readiness, nameof(ModeReadinessViewModel.PrimaryActionText),
            static readiness => readiness.PrimaryActionText, value => _blockedPrimary.Title = value ?? string.Empty, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsPrimaryActionVisible),
            static readiness => readiness.IsPrimaryActionVisible, value => _blockedPrimary.Hidden = !value, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsSecondaryActionVisible),
            static readiness => readiness.IsSecondaryActionVisible, value => _blockedSecondary.Hidden = !value, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsProgressVisible),
            static readiness => readiness.IsProgressVisible, value => { _blockedProgress.Hidden = !value; if (value) _blockedProgress.StartAnimation(null); else _blockedProgress.StopAnimation(null); }, Dispatcher, ReportError));
    }

    private void ApplyLayout()
    {
        if (_released) return;
        bool compact = ViewModel.IsCompactLayout;
        bool detail = ViewModel.IsDetailSurfaceVisible;
        bool listVisible = !compact || !detail;

        _listSurface.Hidden = !listVisible;
        _drawerHost.Hidden = !detail;
        _detailPane.Hidden = !ViewModel.IsSingleTaskSelection;
        _multiPane.Hidden = !ViewModel.IsMultipleTaskSelection;
        _drawerStroke.Hidden = compact;
        _detailBack.Hidden = !compact;
        _multiBack.Hidden = !compact;

        if (compact && detail)
        {
            _drawerWidth.Active = false;
            _drawerFill.Active = true;
        }
        else
        {
            _drawerFill.Active = false;
            _drawerWidth.Constant = detail ? (nfloat)WinoToDoStyle.DrawerWidth : 0;
            _drawerWidth.Active = true;
        }
        _zone.ContentView.NeedsLayout = true;
    }

    // ---- Blocked state (Windows ModeReadinessPanel) ----

    private NSTextField _blockedTitle = null!;
    private NSTextField _blockedMessage = null!;
    private NSButton _blockedPrimary = null!;
    private NSButton _blockedSecondary = null!;
    private NSProgressIndicator _blockedProgress = null!;

    private NSView BuildBlockedPanel()
    {
        var panel = new WinoSurfaceView { Fill = WinoStyle.ZoneFill, Hidden = true };
        _blockedTitle = WinoStyle.Label(string.Empty, WinoStyle.Heading, maximumLines: 2);
        _blockedTitle.Alignment = NSTextAlignment.Center;
        _blockedMessage = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _blockedMessage.Alignment = NSTextAlignment.Center;
        _blockedProgress = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _blockedPrimary = CommandButton(string.Empty, ViewModel.Readiness.PrimaryActionCommand);
        _blockedPrimary.TranslatesAutoresizingMaskIntoConstraints = false;
        _blockedSecondary = CommandButton(Translator.MailEmptyState_ManageAccounts, ViewModel.Readiness.ManageAccountsCommand);
        _blockedSecondary.TranslatesAutoresizingMaskIntoConstraints = false;
        var buttons = WinoLayout.HStack(8, _blockedPrimary, _blockedSecondary);
        var stack = WinoLayout.VStack(10, _blockedTitle, _blockedMessage, _blockedProgress, buttons);
        stack.Alignment = NSLayoutAttribute.CenterX;
        panel.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(panel.CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(panel.CenterYAnchor),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(360)
        ]);
        return panel;
    }

    // ---- Shared helpers ----

    /// <summary>One-way binding of a ViewModel property to a UI update, owned by the page scope.</summary>
    private void Bind<TValue>(string property, Func<ToDoPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<ToDoPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private void OnUI(Action action)
    {
        if (_released) return;
        _ = Dispatcher.ExecuteOnUIThread(() => { if (!_released) action(); });
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    private NSMenuItem MenuItem(string title, Action action, WinoIconGlyph glyph = WinoIconGlyph.None, NSColor? tint = null, bool enabled = true)
    {
        var item = new NSMenuItem(title, (_, _) => action()) { Enabled = enabled };
        if (glyph != WinoIconGlyph.None) item.Image = WinoIcons.Image(glyph, 14, tint);
        return item;
    }

    private void RegisterDebugCommands()
    {
#if DEBUG
        MacDebugBridge.Register("todo-select", args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0]) : 0;
            var rows = _entries.Select((entry, row) => (entry, row)).Where(pair => pair.entry.Item is not null).Select(pair => pair.row).ToList();
            if (index < 0 || index >= rows.Count) return Task.FromResult("no task row " + index);
            _table.SelectRow(rows[index], false);
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("todo-view", async args =>
        {
            var view = Enum.Parse<TaskViewKind>(args[0], true);
            await ViewModel.SelectPresentationSurfaceAsync(view, null!);
            return "ok";
        });
        MacDebugBridge.Register("todo-list", async args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0]) : 0;
            if (index < 0 || index >= ViewModel.TaskLists.Count) return "no list " + index + " of " + ViewModel.TaskLists.Count;
            await ViewModel.SelectPresentationSurfaceAsync(TaskViewKind.All, ViewModel.TaskLists[index]);
            return "ok";
        });
        MacDebugBridge.Register("todo-preview", _ => { ShowPreview(); return Task.FromResult("ok " + _entries.Count); });
        MacDebugBridge.Register("todo-clear", _ => { ViewModel.CloseDetailCommand.Execute(null); return Task.FromResult("ok"); });
        MacDebugBridge.Register("todo-state", _ => Task.FromResult(
            $"groups={ViewModel.TaskGroups.Count} rows={_entries.Count} lists={ViewModel.TaskLists.Count} accounts={ViewModel.Accounts.Count} " +
            $"ready={ViewModel.Readiness.IsReady} loading={ViewModel.IsLoading} empty={ViewModel.IsEmpty} compact={ViewModel.IsCompactLayout} " +
            $"selected={ViewModel.SelectedTasks.Count} view={ViewModel.SelectedView} list={ViewModel.SelectedList?.Title}"));
#endif
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            ReleaseList();
            ReleaseDetail();
        }
        base.Dispose(disposing);
    }
}
