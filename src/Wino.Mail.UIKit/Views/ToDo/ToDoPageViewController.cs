using System.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.ToDo;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Contacts;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.ToDo;

/// <summary>
/// The To Do mode content page on both idioms (Windows ToDoPage, Mac ToDoPageViewController): one Wino zone with the
/// surface header (title in the accent, subtitle), the grouped task cards (<see cref="WinoTaskRowCell"/> with the round
/// checkbox; swipe to complete, mark important or delete; long-press menu) and the quick add bar at the bottom, which rides
/// on the keyboard. Filters, sort, suggestions and the list options are bar menus. The idiom places the task detail: the
/// iPhone pushes it, the iPad shows it as the reader column's drawer. Partial files: List (table and rows) and Menus.
/// </summary>
public abstract partial class ToDoPageViewController : WinoViewController<ToDoPageViewModel>, IShellSearchTarget
{
    private readonly WinoZoneView _zone = new();
    // iPadToDo board (15 Oct 2026): the list title is a compact 17pt semibold header line, not a large accent title.
    private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(17, UIFontWeight.Semibold));
    private readonly UILabel _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
    private readonly WinoQuickAddView _quickAdd = new(Translator.ToDoPage_AddTask);
    private readonly WinoStateView _empty = new(WinoIconGlyph.Checkmark, string.Empty, string.Empty) { Hidden = true };
    private readonly WinoStateView _blocked = new(WinoIconGlyph.Checkmark, string.Empty, string.Empty) { Hidden = true };
    private readonly UIButton _blockedAction = new(UIButtonType.System);
    private UIStackView _header = WinoLayout.VStack(2);
    private bool _released;

    protected ToDoPageViewController(ToDoPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, UIKitNavigationService navigation)
        : base(viewModel, dispatcher, logger)
    {
        Navigation = navigation;
        Title = Translator.ToDoPage_Title;
    }

    protected UIKitNavigationService Navigation { get; }

    /// <summary>The page's ViewModel for the idiom's detail host.</summary>
    protected ToDoPageViewModel PageViewModel => ViewModel;

    /// <summary>True on iPhone: the surface title is the navigation bar's large title instead of the header's.</summary>
    protected abstract bool UsesNavigationTitle { get; }

    /// <summary>True when the page owns a UISearchController (iPhone); the iPad uses the shell's toolbar search.</summary>
    protected abstract bool UsesSearchController { get; }

    /// <summary>A task row was tapped; the selection is already in the ViewModel.</summary>
    protected abstract void TaskActivated(TaskItemViewModel task);

    public string? SearchPlaceholder => Translator.ToDoPage_FilterTasks;

    protected bool IsReleased => _released;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        root.AddSubview(_zone);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            _zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
            _zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            _zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
            _zone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
        var content = _zone.ContentView;

        _title.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        _header = WinoLayout.Insets(WinoLayout.VStack(2, _title, _subtitle), 14, 18, 6, 18);
        _header.Alignment = UIStackViewAlignment.Fill;

        BuildTable();
        var table = Table;

        _quickAdd.Submitted += (_, _) => SubmitQuickAdd();
        _quickAdd.TextChanged += (_, _) => ViewModel.ComposerText = _quickAdd.Text;
        _quickAdd.FocusChanged += (_, _) =>
        {
            if (_quickAdd.IsFocused) ViewModel.IsComposerExpanded = true;
            else if (string.IsNullOrWhiteSpace(ViewModel.ComposerText)) ViewModel.IsComposerExpanded = false;
        };

        content.AddSubview(_header);
        content.AddSubview(table);
        content.AddSubview(_quickAdd);
        _header.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _header.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _header.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _header.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            table.TopAnchor.ConstraintEqualTo(_header.BottomAnchor),
            table.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 4),
            table.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -4),
            table.BottomAnchor.ConstraintEqualTo(_quickAdd.TopAnchor, -6),
            _quickAdd.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 12),
            _quickAdd.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -12),
            // The quick add bar rides on the keyboard; without one it rests on the zone's foot.
            _quickAdd.BottomAnchor.ConstraintLessThanOrEqualTo(content.BottomAnchor, -12),
            _quickAdd.BottomAnchor.ConstraintLessThanOrEqualTo(root.KeyboardLayoutGuide.TopAnchor, -8)
        ]);
        var rest = _quickAdd.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -12);
        rest.Priority = 700;
        rest.Active = true;

        _empty.UserInteractionEnabled = false;
        content.AddSubview(_empty);
        _empty.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _empty.TopAnchor.ConstraintEqualTo(table.TopAnchor),
            _empty.BottomAnchor.ConstraintEqualTo(table.BottomAnchor),
            _empty.LeadingAnchor.ConstraintEqualTo(table.LeadingAnchor),
            _empty.TrailingAnchor.ConstraintEqualTo(table.TrailingAnchor)
        ]);

        // Windows ModeReadinessPanel.
        _blockedAction.PrimaryActionTriggered += (_, _) =>
        {
            if (ViewModel.Readiness.PrimaryActionCommand.CanExecute(null)) ViewModel.Readiness.PrimaryActionCommand.Execute(null);
        };
        _blocked.Add(_blockedAction);
        _blocked.BackgroundColor = WinoStyle.ZoneFill;
        _blocked.Place(content);
        View = root;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _title.Hidden = UsesNavigationTitle;
        ModeBarItems.AppendRight(NavigationItem, OptionsBarItem(), FilterBarItem());
        if (UsesSearchController) BuildSearchController();
        SetContentScrollView(Table, NSDirectionalRectEdge.Top);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BindList();
        Bind(nameof(ViewModel.SelectedSurfaceTitle), static vm => vm.SelectedSurfaceTitle, value =>
        {
            _title.Text = value ?? string.Empty;
            // NavigationItem.Title, not Title: a tab root's Title would rename the To Do tab.
            if (UsesNavigationTitle) NavigationItem.Title = value ?? Translator.ToDoPage_Title;
        });
        Bind(nameof(ViewModel.SelectedSurfaceSubtitle), static vm => vm.SelectedSurfaceSubtitle, value => _subtitle.Text = value ?? string.Empty);
        Bind(nameof(ViewModel.HasSurfaceSubtitle), static vm => vm.HasSurfaceSubtitle, value => _subtitle.Hidden = !value);
        Bind(nameof(ViewModel.IsEmpty), static vm => vm.IsEmpty, value => _empty.Hidden = !value);
        Bind(nameof(ViewModel.EmptyStateTitle), static vm => vm.EmptyStateTitle, value => _empty.Title.Text = value ?? string.Empty);
        Bind(nameof(ViewModel.EmptyStateBody), static vm => vm.EmptyStateBody, value => _empty.Caption.Text = value ?? string.Empty);
        Bind(nameof(ViewModel.IsQuickAddVisible), static vm => vm.IsQuickAddVisible, value => _quickAdd.Hidden = !value);
        Bind(nameof(ViewModel.ComposerPlaceholder), static vm => vm.ComposerPlaceholder, value => _quickAdd.Placeholder = value ?? string.Empty);
        Bind(nameof(ViewModel.ComposerText), static vm => vm.ComposerText, value => _quickAdd.Text = value ?? string.Empty);
        Bind(nameof(ViewModel.CanCreateTask), static vm => vm.CanCreateTask, value => _quickAdd.Enabled = value);
        Bind(nameof(ViewModel.IsComposerExpanded), static vm => vm.IsComposerExpanded, value => _quickAdd.ShowButton = value);
        Bindings.Own(new CommandBinding(ViewModel.AddTaskCommand, () => null, _ => { }, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(ViewModel.Readiness, nameof(ModeReadinessViewModel.IsBlocked),
            static readiness => readiness.IsBlocked, _ => ApplyReadiness(), Dispatcher, ReportError));
        ViewModel.Readiness.PropertyChanged += ReadinessChanged;
        Bindings.Own(new ActionDisposable(() => ViewModel.Readiness.PropertyChanged -= ReadinessChanged));
        ViewModel.TaskComposerFocusRequested += ComposerFocusRequested;
        Bindings.Own(new ActionDisposable(() => ViewModel.TaskComposerFocusRequested -= ComposerFocusRequested));
        PrepareLayout();
#if DEBUG
        RegisterDebugCommands();
#endif
        return base.InitializeAsync(mode, parameter);
    }

    /// <summary>The idiom's layout hook before the ViewModel activates (the iPhone switches the shared layout to compact).</summary>
    protected virtual void PrepareLayout() { }

    protected override Task DeactivateAsync()
    {
        ReleaseList();
        return base.DeactivateAsync();
    }

    public override async Task ReleaseAsync()
    {
        _released = true;
        await base.ReleaseAsync();
    }

    private void ComposerFocusRequested(object? sender, Wino.Core.Domain.Models.TaskComposerFocusRequestedEventArgs args) => OnUI(_quickAdd.Focus);

    private void SubmitQuickAdd()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.ComposerText) || !ViewModel.AddTaskCommand.CanExecute(null)) return;
        Observe(ViewModel.AddTaskCommand.ExecuteAsync(null));
    }

    private void ReadinessChanged(object? sender, PropertyChangedEventArgs args) => OnUI(ApplyReadiness);

    private void ApplyReadiness()
    {
        var readiness = ViewModel.Readiness;
        _blocked.Hidden = !readiness.IsBlocked;
        if (!readiness.IsBlocked) return;
        _blocked.Title.Text = readiness.Title ?? string.Empty;
        _blocked.Caption.Text = readiness.Message ?? string.Empty;
        _blockedAction.SetTitle(readiness.PrimaryActionText ?? string.Empty, UIControlState.Normal);
        _blockedAction.Hidden = !readiness.IsPrimaryActionVisible;
    }

    /// <summary>A task detail over this page's ViewModel; the idiom hosts it.</summary>
    protected ToDoTaskDetailViewController CreateDetail(bool showsCloseButton)
        => new(ViewModel, Dispatcher, ReportError, PresentDueDatePicker, showsCloseButton);

    // ---- Search (the iPhone search controller and the iPad toolbar field filter the surface) ----

    private UISearchController? _searchController;
    private SearchHandler? _searchHandler;

    private void BuildSearchController()
    {
        _searchHandler = new SearchHandler(this);
        _searchController = new UISearchController((UIViewController?)null)
        {
            ObscuresBackgroundDuringPresentation = false,
            SearchResultsUpdater = _searchHandler
        };
        _searchController.SearchBar.Placeholder = Translator.ToDoPage_FilterTasks;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = true;
        DefinesPresentationContext = true;
    }

    public Task SearchTextChangedAsync(string text)
    {
        ViewModel.FilterText = text ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task SearchSubmittedAsync(string text) => SearchTextChangedAsync(text);

    public Task SearchClearedAsync() => SearchTextChangedAsync(string.Empty);

    private sealed class SearchHandler(ToDoPageViewController owner) : UISearchResultsUpdating
    {
        public override void UpdateSearchResultsForSearchController(UISearchController searchController)
            => owner.ViewModel.FilterText = searchController.SearchBar.Text ?? string.Empty;
    }

    // ---- Helpers ----

    protected void OnUI(Action action)
    {
        if (_released) return;
        _ = Dispatcher.ExecuteOnUIThread(() => { if (!_released) action(); });
    }

    protected async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            ReleaseList();
        }
        base.Dispose(disposing);
    }
}
