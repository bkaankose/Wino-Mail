using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The phone message list for one folder (Mail on iPhone, over the shared <see cref="MailListPageViewModel"/>): a table
/// of Wino mail rows over the shared projection inside one Wino zone, pushed on the Mail stack over Mailboxes. A tap
/// selects through the ViewModel, whose active item opens the reader (pushed) or, for a draft, the composer (a full-screen
/// sheet). Partial files: List (table, projection, selection, header and bars), Commands (swipe actions, long-press menus,
/// edit mode, Move), Search (search controller, scope, tokens and suggestions), Reader (the pushed reader and composer)
/// and Debug (bridge commands).
/// </summary>
public sealed partial class MailListPageViewController : WinoViewController<MailListPageViewModel>,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<DisposeRenderingFrameRequested>,
    IRecipient<ComposeDetachedDraftRequested>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IPhoneFloatingActionPage
{
    private readonly IFolderService _folderService;
    private readonly IPreferencesService _preferences;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly UIKitSenderPictureProvider _pictures;
    private readonly PhoneShellHost _host;
    private readonly IMailShellClient? _mailClient;
    private readonly MailListPage _page = new();
    private bool _registered;
    private bool _released;
    private bool _keepAliveOnPop;

    public MailListPageViewController(MailListPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        IFolderService folderService, IWinoAccountIntelligenceSnapshotService entitlementService,
        UIKitSenderPictureProvider pictures, PhoneShellHost host, IMailShellClient mailClient)
        : base(viewModel, dispatcher, logger)
    {
        _mailClient = mailClient;
        _folderService = folderService;
        _entitlementService = entitlementService;
        _pictures = pictures;
        _host = host;
        _preferences = viewModel.PreferencesService;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
    }

    public override void LoadView()
    {
        ViewModel.MailCollection.CoreDispatcher = Dispatcher;
        BuildList(_page);
        BuildSearch();
        BuildBarItems();
        View = _page;
        // The table sits in the zone under a compact bar and never drives it: no content scroll view tracking, so pull to
        // refresh moves only the rows (the bar and the header row stay put).
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        RegisterRecipients();
        BindList();
        BindSearch();
        ViewModel.OnNavigatedTo(mode, parameter);
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
    }

    protected override async Task DeactivateAsync()
    {
        UnregisterRecipients();
        DismissOwnSheets();
        ReleaseList();
        await ViewModel.DeactivateAsync(NavigationMode.New, null);
    }

    /// <summary>
    /// Popping the list back to Mailboxes keeps it alive (the shell still holds it as its content), so tapping the same
    /// folder shows it again instantly, like Mail on iPhone. The router releases it when another folder or mode replaces it.
    /// </summary>
    public override void ViewDidDisappear(bool animated)
    {
        // Leaving the list ends edit mode, which also gives the tab bar back.
        if (IsMovingFromParentViewController && _selectionMode) ViewModel.IsMultiSelectionModeEnabled = false;
        _keepAliveOnPop = IsMovingFromParentViewController && !_released;
        try { base.ViewDidDisappear(animated); }
        finally { _keepAliveOnPop = false; }
    }

    public override Task ReleaseAsync()
    {
        if (_keepAliveOnPop) return Task.CompletedTask;
        _released = true;
        return base.ReleaseAsync();
    }

    /// <summary>True while the page is active and can be shown again by the shell.</summary>
    internal bool IsRetained => IsActive && !_released;

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        // Back from the reader or the composer: the message is no longer "open", so a tap on it opens it again.
        if (!IsMovingToParentViewController && !_selectionMode) ClearSelection();
        UpdateTitle();
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

    private void UpdateTitle()
    {
        if (!IsViewLoaded) return;
        // NavigationItem.Title, not Title: the list is the Mail stack's root, and its Title would rename the Mail tab.
        NavigationItem.Title = _selectionMode
            ? (ViewModel.SelectedItemsCount > 0 ? string.Format(Translator.MailsSelected, ViewModel.SelectedItemsCount) : Translator.NoMailSelected)
            : ViewModel.ActiveFolder?.FolderName ?? Translator.IOS_MailboxesTitle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            UnregisterRecipients();
            ReleaseList();
        }
        base.Dispose(disposing);
    }
}
