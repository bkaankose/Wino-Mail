using System.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Contacts;

/// <summary>
/// The Contacts mode content page on both idioms (Windows ContactsPage, Mac ContactsPageViewController): the
/// letter-indexed contact list in one Wino zone with search, pull to refresh, swipe and long-press actions, paging and
/// the blocked (readiness) state. The idiom decides where the contact card goes: the iPhone pushes it, the iPad shows it
/// in the reader column. Partial files: List (table, sections, search) and Menus (context menu, swipes, filters).
/// </summary>
public abstract partial class ContactsPageViewController : WinoViewController<ContactsPageViewModel>, IShellSearchTarget
{
    private readonly IPictureStorageService _pictures;
    private readonly Dictionary<Guid, UIImage?> _pictureCache = new();
    private readonly WinoZoneView _zone = new();
    private readonly WinoStateView _blocked = new(WinoIconGlyph.People, string.Empty, string.Empty) { Hidden = true };
    private readonly UIButton _blockedAction = new(UIButtonType.System);
    private bool _released;

    protected ContactsPageViewController(ContactsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        UIKitNavigationService navigation, IPictureStorageService pictures, IExternalLauncher launcher)
        : base(viewModel, dispatcher, logger)
    {
        Navigation = navigation;
        _pictures = pictures;
        Launcher = launcher;
        Title = Translator.ContactsPage_Title;
    }

    protected UIKitNavigationService Navigation { get; }

    protected IExternalLauncher Launcher { get; }

    /// <summary>The page's ViewModel for the idiom's card host.</summary>
    protected ContactsPageViewModel PageViewModel => ViewModel;

    /// <summary>True when the page owns a UISearchController (iPhone); the iPad uses the shell's toolbar search.</summary>
    protected abstract bool UsesSearchController { get; }

    /// <summary>True when the filters (address books, lists, categories) are a bar menu (iPhone); the iPad sidebar shows them.</summary>
    protected abstract bool ShowsFilterMenu { get; }

    /// <summary>False leaves New contact out of the bar (the iPhone's floating button creates contacts).</summary>
    protected virtual bool ShowsAddButton => true;

    /// <summary>A contact row was tapped; the selection is already in the ViewModel.</summary>
    protected abstract void ContactActivated(AccountContactViewModel contact);

    /// <summary>The ViewModel's selected contact changed (the iPad card follows it).</summary>
    protected virtual void SelectedContactChanged(AccountContactViewModel? contact) { }

    public string? SearchPlaceholder => Translator.ContactsPage_SearchPlaceholder;

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
        BuildList(_zone.ContentView);

        // Windows ModeReadinessPanel: covers the list while People cannot be used.
        _blockedAction.PrimaryActionTriggered += (_, _) =>
        {
            if (ViewModel.Readiness.PrimaryActionCommand.CanExecute(null)) ViewModel.Readiness.PrimaryActionCommand.Execute(null);
        };
        _blocked.Add(_blockedAction);
        _blocked.BackgroundColor = WinoStyle.ZoneFill;
        _blocked.Place(_zone.ContentView);
        View = root;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var items = new List<UIBarButtonItem>();
        if (ShowsAddButton) items.Add(ModeBarItems.Button(WinoIconGlyph.PersonAdd, Translator.ContactsPane_NewContact, AddContact));
        if (ShowsFilterMenu) items.Add(FilterBarItem());
        ModeBarItems.AppendRight(NavigationItem, items.ToArray());
        if (UsesSearchController) BuildSearchController();
        SetContentScrollView(Table, NSDirectionalRectEdge.Top);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BindList();
        Bind(nameof(ViewModel.SelectedContact), static vm => (AccountContactViewModel?)vm.SelectedContact, contact =>
        {
            SelectRow(contact);
            SelectedContactChanged(contact);
        });
        Bindings.Own(new Wino.Presentation.Bindings.PropertyBinding<Wino.Core.ViewModels.Data.ModeReadinessViewModel, bool>(
            ViewModel.Readiness, nameof(ViewModel.Readiness.IsBlocked), static readiness => readiness.IsBlocked, _ => ApplyReadiness(), Dispatcher, ReportError));
        ViewModel.Readiness.PropertyChanged += ReadinessChanged;
        Bindings.Own(new ActionDisposable(() => ViewModel.Readiness.PropertyChanged -= ReadinessChanged));
#if DEBUG
        RegisterDebugCommands();
#endif
        return base.InitializeAsync(mode, parameter);
    }

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

    /// <summary>True once the page left the shell for good.</summary>
    protected bool IsReleased => _released;

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

    protected void AddContact()
    {
        if (ViewModel.AddContactCommand.CanExecute(null)) ViewModel.AddContactCommand.Execute(null);
    }

    /// <summary>The contact's stored photo, cached per file id; null falls back to initials.</summary>
    internal UIImage? LoadPicture(AccountContactViewModel contact)
    {
        if (contact.ContactPictureFileId is not { } id) return null;
        if (_pictureCache.TryGetValue(id, out var cached)) return cached;
        UIImage? image = null;
        try
        {
            var path = _pictures.GetPicturePath(PictureKind.Contact, id);
            if (File.Exists(path)) image = UIImage.FromFile(path);
        }
        catch (Exception exception) { ReportError(exception); }
        _pictureCache[id] = image;
        return image;
    }

    /// <summary>A card over this page's ViewModel (the idiom hosts it).</summary>
    protected ContactCardViewController CreateCard(bool showsEmptyState)
        => new(ViewModel, Dispatcher, Launcher, LoadPicture, ReportError, showsEmptyState);

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
