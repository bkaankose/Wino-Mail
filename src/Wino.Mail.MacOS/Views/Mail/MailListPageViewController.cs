using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Mail mode content page: a horizontal split with the mail list (header, search scope bar,
/// view-based table over the shared projection) and the reading pane, which hosts the rendering,
/// compose and idle pages routed to <see cref="MacPageHost.RenderingFrame"/>.
/// Partial files: List (table, projection, selection), ReadingPane (IRenderingFrameHost),
/// Commands (toolbar, menus, hover, swipe, Move popover) and Search (toolbar search and scope bar).
/// </summary>
public sealed partial class MailListPageViewController : WinoViewController<MailListPageViewModel>,
    IShellCommandTarget,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<DisposeRenderingFrameRequested>,
    IRecipient<ComposeDetachedDraftRequested>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<WinoIntelligenceEntitlementChanged>
{
    private const string SplitAutosaveName = "WinoMailListReaderSplit";
    private const double DefaultListWidth = 386;

    private readonly AppKitNavigationService _navigation;
    private readonly IFolderService _folderService;
    private readonly IPreferencesService _preferences;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly IKeyboardShortcutService _shortcuts;
    private NSSplitViewController _split = null!;
    private WinoZoneView _listZone = null!;
    private WinoZoneView _readerZone = null!;
    private bool _registered;
    private bool _released;

    public MailListPageViewController(MailListPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        AppKitNavigationService navigation, IFolderService folderService,
        IWinoAccountIntelligenceSnapshotService entitlementService, IKeyboardShortcutService shortcuts)
        : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
        _folderService = folderService;
        _entitlementService = entitlementService;
        _shortcuts = shortcuts;
        _preferences = viewModel.PreferencesService;
    }

    public override void LoadView()
    {
        ViewModel.MailCollection.CoreDispatcher = Dispatcher;

        var root = new MailListPage();
        _split = new NSSplitViewController { SplitView = new GutterSplitView() };
        _split.SplitView.IsVertical = true;

        // Windows: MailListContainer margin 6,2 and the reader column to its right; the zones float
        // over the theme backdrop with a 6pt gutter (the invisible divider) between them.
        var listHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _listZone = new WinoZoneView();
        WinoLayout.Fill(_listZone, listHost, 2, WinoStyle.ZoneGutter, 8, 0);
        WinoLayout.Fill(BuildListPane(), _listZone.ContentView);
        var readerHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _readerZone = new WinoZoneView { Fill = WinoThemeSurfaces.ReadingPaneFill };
        WinoLayout.Fill(_readerZone, readerHost, 3, 0, 8, 8);
        WinoLayout.Fill(BuildReadingPane(), _readerZone.ContentView);

        var listController = new NSViewController { View = listHost };
        var readerController = new NSViewController { View = readerHost };
        var listItem = NSSplitViewItem.FromViewController(listController);
        listItem.MinimumThickness = 270 + (nfloat)WinoStyle.ZoneGutter;
        listItem.HoldingPriority = 260;
        listItem.CanCollapse = false;
        var readerItem = NSSplitViewItem.FromViewController(readerController);
        readerItem.MinimumThickness = 375;
        readerItem.HoldingPriority = 250;
        _split.AddSplitViewItem(listItem);
        _split.AddSplitViewItem(readerItem);
        bool hasSavedWidth = Foundation.NSUserDefaults.StandardUserDefaults[$"NSSplitView Subview Frames {SplitAutosaveName}"] is not null;
        _split.SplitView.AutosaveName = SplitAutosaveName;

        AddChildViewController(_split);
        WinoLayout.Fill(_split.View, root);
        View = root;

        if (!hasSavedWidth)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (!_released && _split.SplitView.ArrangedSubviews.Length == 2)
                    _split.SplitView.SetPositionOfDivider((nfloat)DefaultListWidth, 0);
            });
        }
    }

    /// <summary>Puts the divider at the given list width (debug bridge).</summary>
    internal void SetListWidth(double width)
    {
        if (_split?.SplitView.ArrangedSubviews.Length == 2) _split.SplitView.SetPositionOfDivider((nfloat)width, 0);
    }

    /// <summary>A split view whose divider is the transparent 6pt gutter between the two zones.</summary>
    private sealed class GutterSplitView : NSSplitView
    {
        public override nfloat DividerThickness => (nfloat)WinoStyle.ZoneGutter;

        public override NSColor DividerColor => NSColor.Clear;

        public override void DrawDivider(CoreGraphics.CGRect rect)
        {
        }
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        RegisterRecipients();
        _navigation.AttachRenderingHost(this);
        BindList();
        BindSearch();
        ViewModel.OnNavigatedTo(mode, parameter!);
        ShowIdleContent();
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
    }

    protected override async Task DeactivateAsync()
    {
        UnregisterRecipients();
        _navigation.DetachRenderingHost(this);
        CloseMovePopover();
        CloseSearchFilters();
        await ReleaseReadingPaneAsync();
        ReleaseList();
        await ViewModel.DeactivateAsync(NavigationMode.New, null!);
    }

    public override async Task ReleaseAsync()
    {
        _released = true;
        await base.ReleaseAsync();
    }

    private void RegisterRecipients()
    {
        if (_registered) return;
        _registered = true;
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<ActiveMailItemChangedEvent>(this);
        messenger.Register<ClearMailSelectionsRequested>(this);
        messenger.Register<DisposeRenderingFrameRequested>(this);
        messenger.Register<ComposeDetachedDraftRequested>(this);
        messenger.Register<SelectMailItemContainerEvent>(this);
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
        Observe(RefreshIntelligenceEntitlementAsync());
    }

    private void UnregisterRecipients()
    {
        if (!_registered) return;
        _registered = false;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>One-way binding of a ViewModel property to a UI update, owned by the page scope.</summary>
    private void Bind<TValue>(string property, Func<MailListPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<MailListPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            UnregisterRecipients();
            _navigation.DetachRenderingHost(this);
            CloseSearchFilters();
            ReleaseList();
            DisposeReadingPane();
        }
        base.Dispose(disposing);
    }
}
