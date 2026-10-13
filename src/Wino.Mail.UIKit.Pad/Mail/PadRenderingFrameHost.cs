using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Mail;

/// <summary>
/// The reader column (the Mac reading pane, <c>IRenderingFrameHost</c>): the root of the column's navigation stack, so
/// the toolbar row's search, briefing, What's New and avatar items stay put while pages change. Rendering-frame pages
/// (reader, composer, idle) sit inside a rounded Wino zone; the router activates and releases them, this host only places
/// them. With nothing routed the zone shows the mode's idle glyph; a multiple selection covers the page with
/// <see cref="MailMultiSelectionView"/>. A docked page's own bar items (the composer's Send, More, Attach and Open in
/// new window) sit in the column's command row at the top of the zone (the Mac composer command row), so the toolbar
/// row keeps its full-width search field. A composer can leave for its own window scene through <see cref="IReaderFrameHost"/>.
/// </summary>
public sealed class PadRenderingFrameHost : UIViewController, IReaderFrameHost
{
    /// <summary>The Mac Revision 2 zone radius.</summary>
    public const double ZoneRadius = 8;

    private readonly PadShellHost _host;
    private readonly Func<UIKitNavigationService?> _navigation;
    private readonly WinoZoneView _zone = new() { CornerRadius = ZoneRadius };
    private readonly UIView _pageHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, ClipsToBounds = true };
    private readonly UIToolbar _commandRow = new() { Hidden = true };
    private readonly WinoStateView _idle = new(WinoIconGlyph.MailFilled, string.Empty, null, WinoStyle.TertiaryText);
    private readonly MailMultiSelectionView _multiSelection = new() { Hidden = true };
    private UIViewController? _page;

    public PadRenderingFrameHost(PadShellHost host, Func<UIKitNavigationService?> navigation)
    {
        _host = host;
        _navigation = navigation;
    }

    /// <summary>The glyph of the idle state; the shell sets the mode's glyph.</summary>
    public WinoIconGlyph IdleGlyph { get; private set; } = WinoIconGlyph.MailFilled;

    /// <summary>Shows <paramref name="glyph"/> while the frame is empty.</summary>
    public void SetIdleGlyph(WinoIconGlyph glyph)
    {
        IdleGlyph = glyph;
        _idle.Icon.Icon = glyph;
    }

    /// <summary>The page in the frame, or null.</summary>
    public UIViewController? Page => _page;

    /// <summary>The multiple-selection summary over the frame; the mail page fills and shows it.</summary>
    public MailMultiSelectionView MultiSelection => _multiSelection;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        _zone.Fill = WinoThemeSurfaces.ReadingPaneFill;
        root.AddSubview(_zone);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            _zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
            _zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            _zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
            _zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
        _idle.Place(_zone.ContentView);
        var appearance = new UIToolbarAppearance();
        appearance.ConfigureWithTransparentBackground();
        _commandRow.StandardAppearance = appearance;
        _commandRow.CompactAppearance = appearance;
        // A hidden command row collapses, so a page without bar items fills the zone.
        var column = new UIStackView([_commandRow, _pageHost]) { Axis = UILayoutConstraintAxis.Vertical };
        WinoLayout.Fill(column, _zone.ContentView);
        WinoLayout.Fill(_multiSelection, _zone.ContentView);
        View = root;
    }

    /// <summary>Places a routed page in the frame. The router releases the previous page and activates this one.</summary>
    public void Show(UIViewController page)
    {
        _ = View;
        if (ReferenceEquals(_page, page)) return;

        Detach(_page);
        _page = page;
        if (page is IReaderFrameChild child) child.FrameHost = this;
        AddChildViewController(page);
        if (page.View is { } view) WinoLayout.Fill(view, _pageHost);
        page.DidMoveToParentViewController(this);
        _idle.Hidden = true;
        _pageHost.Hidden = false;
        // A docked page's own bar items join the command row on the leading side once the page has built them.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() => ShowPageItems(page));
    }

    private void ShowPageItems(UIViewController page)
    {
        if (!ReferenceEquals(_page, page)) return;
        var items = page.NavigationItem.RightBarButtonItems ?? [];
        // Primary first on the leading side (Send, More, Attach, Open in new window), as on the Mac command row.
        _commandRow.SetItems([.. items, UIBarButtonItem.FlexibleSpaceItem], false);
        _commandRow.Hidden = items.Length == 0;
    }

    /// <summary>Empties the frame: releases the page (drafts save) and shows the idle glyph.</summary>
    public async Task ClearAsync()
    {
        var page = _page;
        if (page is null) return;

        Detach(page);
        _page = null;
        _idle.Hidden = false;
        _navigation()?.DetachRenderingPage(page);
        await ReleaseAndDisposeAsync(page);
    }

    private void Detach(UIViewController? page)
    {
        if (page is null) return;

        if (page is IReaderFrameChild child && ReferenceEquals(child.FrameHost, this)) child.FrameHost = null;
        _commandRow.SetItems([], false);
        _commandRow.Hidden = true;
        page.WillMoveToParentViewController(null);
        page.View?.RemoveFromSuperview();
        page.RemoveFromParentViewController();
    }

    private static async Task ReleaseAndDisposeAsync(UIViewController page)
    {
        try { if (page is IWinoViewController routed) await routed.ReleaseAsync(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "A reader frame page could not be released."); }
        page.Dispose();
    }

    #region IReaderFrameHost

    public bool CanOpenInNewWindow => _host.CanOpenPopOut;

    public Task<bool> OpenInNewWindowAsync(UIViewController child)
    {
        if (!ReferenceEquals(child, _page) || !_host.CanOpenPopOut) return Task.FromResult(false);

        Detach(child);
        _page = null;
        _idle.Hidden = false;
        _navigation()?.DetachRenderingPage(child);
        if (_host.RequestPopOut(child)) return Task.FromResult(true);

        // The scene request failed synchronously: the page comes back.
        Show(child);
        return Task.FromResult(false);
    }

    #endregion
}
