using AppKit;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Contacts;

/// <summary>
/// Contacts mode content page (Windows ContactsPage): one Wino zone holding the alphabetised
/// contact list on the left and the 348 pt detail pane on the right. The detail pane hides below
/// the Windows medium breakpoint (window narrower than 1008). Partial files: List (search,
/// grouped table, selection, context menu, load more) and Detail (selected contact pane).
/// </summary>
public sealed partial class ContactsPageViewController : WinoViewController<ContactsPageViewModel>, IShellSearchTarget
{
    private const double DetailWidth = 348;
    private const double WideBreakpoint = 1008;

    private readonly AppKitNavigationService _navigation;
    private readonly IPictureStorageService _pictures;
#if DEBUG
    private readonly INotificationBuilder _notifications;
#endif
    private readonly Dictionary<Guid, NSImage?> _pictureCache = new();
    private WinoZoneView _zone = null!;
    private NSView _detailPane = null!;
    private NSLayoutConstraint _detailWidth = null!;
    private NSLayoutConstraint _listTrailing = null!;
    private NSView _readinessOverlay = null!;
    private NSTextField _readinessTitle = null!;
    private NSTextField _readinessMessage = null!;
    private bool _released;

    /// <summary>Contact to bring into view when the page returns from the editor (the router does not pass NavigationResult).</summary>
    internal static Guid? PendingSelection { get; set; }

    public ContactsPageViewController(ContactsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        AppKitNavigationService navigation, IPictureStorageService pictures, INotificationBuilder notifications)
        : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
        _pictures = pictures;
#if DEBUG
        _notifications = notifications;
#endif
    }

    public override void LoadView()
    {
        var root = new NSView();
        _zone = new WinoZoneView();
        WinoLayout.Fill(_zone, root, 2, 4, 7, 7);
        var content = _zone.ContentView;

        var list = BuildListPane();
        _detailPane = BuildDetailPane();
        var stroke = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke };
        content.AddSubview(list);
        content.AddSubview(_detailPane);
        content.AddSubview(stroke);
        _detailWidth = _detailPane.WidthAnchor.ConstraintEqualTo((nfloat)DetailWidth);
        _listTrailing = list.TrailingAnchor.ConstraintEqualTo(_detailPane.LeadingAnchor);
        NSLayoutConstraint.ActivateConstraints(
        [
            list.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            list.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            list.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            _listTrailing,
            _detailPane.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _detailPane.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _detailPane.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            _detailWidth,
            stroke.LeadingAnchor.ConstraintEqualTo(_detailPane.LeadingAnchor),
            stroke.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            stroke.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            stroke.WidthAnchor.ConstraintEqualTo(1)
        ]);

        // Blocked state (Windows ModeReadinessPanel) covers both panes while People cannot be used.
        _readinessOverlay = new WinoSurfaceView { Fill = WinoStyle.ZoneFill, Hidden = true };
        _readinessTitle = WinoStyle.Label(string.Empty, WinoStyle.Heading, maximumLines: 0);
        _readinessTitle.Alignment = NSTextAlignment.Center;
        _readinessMessage = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _readinessMessage.Alignment = NSTextAlignment.Center;
        var readinessStack = WinoLayout.VStack(8, _readinessTitle, _readinessMessage);
        readinessStack.Alignment = NSLayoutAttribute.CenterX;
        _readinessOverlay.AddSubview(readinessStack);
        NSLayoutConstraint.ActivateConstraints(
        [
            readinessStack.CenterXAnchor.ConstraintEqualTo(_readinessOverlay.CenterXAnchor),
            readinessStack.CenterYAnchor.ConstraintEqualTo(_readinessOverlay.CenterYAnchor),
            readinessStack.WidthAnchor.ConstraintLessThanOrEqualTo(420)
        ]);
        WinoLayout.Fill(_readinessOverlay, content);
        View = root;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BindList();
        BindDetail();
        Bind(nameof(ViewModel.Readiness), vm => vm.Readiness.IsBlocked, ApplyReadiness);
        ViewModel.Readiness.PropertyChanged += ReadinessChanged;
        Bindings.Own(new ActionDisposable(() => ViewModel.Readiness.PropertyChanged -= ReadinessChanged));
        ViewModel.OnNavigatedTo(mode, parameter!);
        RegisterDebugCommands();
        if (PendingSelection is { } pending)
        {
            PendingSelection = null;
            Observe(ViewModel.LoadAndSelectContactAsync(pending));
        }
        return Task.CompletedTask;
    }

    protected override Task DeactivateAsync()
    {
        ReleaseList();
        ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
        return Task.CompletedTask;
    }

    public override async Task ReleaseAsync()
    {
        _released = true;
        await base.ReleaseAsync();
    }

    public override void ViewDidLayout()
    {
        base.ViewDidLayout();
        var width = View.Window?.Frame.Width ?? View.Bounds.Width + 260;
        bool wide = width >= WideBreakpoint;
        if (_detailPane.Hidden == wide)
        {
            _detailPane.Hidden = !wide;
            _detailWidth.Constant = wide ? (nfloat)DetailWidth : 0;
        }
    }

    private void ReadinessChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(ViewModel.Readiness.IsBlocked) or nameof(ViewModel.Readiness.Readiness))) return;
        OnUI(() => ApplyReadiness(ViewModel.Readiness.IsBlocked));
    }

    private void ApplyReadiness(bool blocked)
    {
        _readinessOverlay.Hidden = !blocked;
        if (!blocked) return;
        _readinessTitle.StringValue = ViewModel.Readiness.Title ?? string.Empty;
        _readinessMessage.StringValue = ViewModel.Readiness.Message ?? string.Empty;
    }

    /// <summary>The contact's stored photo as an image, cached per file id; null falls back to initials.</summary>
    private NSImage? LoadPicture(AccountContactViewModel contact)
    {
        if (contact.ContactPictureFileId is not { } id) return null;
        if (_pictureCache.TryGetValue(id, out var cached)) return cached;
        NSImage? image = null;
        try
        {
            var path = _pictures.GetPicturePath(Wino.Core.Domain.Enums.PictureKind.Contact, id);
            if (File.Exists(path)) image = new NSImage(path);
        }
        catch (Exception exception) { ReportError(exception); }
        _pictureCache[id] = image;
        return image;
    }

    private void Bind<TValue>(string property, Func<ContactsPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<ContactsPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

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
            ReleaseList();
        }
        base.Dispose(disposing);
    }
}
