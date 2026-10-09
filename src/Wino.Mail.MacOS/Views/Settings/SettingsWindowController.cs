using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// The Settings window (Cmd+,) in the Wino look: the theme backdrop paints the whole window, the
/// 230pt sidebar floats on a blurred veil over it, and the page sits in a Wino zone (radius 7) with
/// the Windows breadcrumb title and description above its cards. Navigation state lives in
/// <see cref="SettingsWindowPresenter"/>; this class owns only window chrome.
/// </summary>
public sealed class SettingsWindowController : NSWindowController
{
    public const string AutosaveName = "WinoSettingsWindow";
    private const string ToolbarIdentifier = "WinoSettingsToolbar";
    private const string NavigationItemIdentifier = "WinoSettingsNavigation";
    private const double SidebarWidth = 230;

    private readonly SettingsSidebarViewController _sidebar = new();
    private readonly NSSegmentedControl _navigation;
    private readonly WinoZoneView _zone = new();
    private readonly NSView _pageHost = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSTextField _crumb;
    private readonly NSTextField _title;
    private readonly NSTextField _description;
    private NSViewController? _page;
    private ToolbarDelegate? _toolbarDelegate;

    public SettingsWindowController() : base(CreateWindow())
    {
        var window = Window!;
        _navigation = new NSSegmentedControl
        {
            SegmentCount = 2,
            TrackingMode = NSSegmentSwitchTracking.Momentary,
            SegmentStyle = NSSegmentStyle.Separated
        };
        _navigation.SetImage(WinoIcons.Image(WinoIconGlyph.ArrowLeft, 13, accessibilityDescription: Translator.Buttons_Back), 0);
        _navigation.SetImage(WinoIcons.Image(WinoIconGlyph.ArrowRight, 13, accessibilityDescription: "Forward"), 1);
        _navigation.SetToolTip(Translator.Buttons_Back, 0);
        _navigation.SetToolTip("Forward", 1);
        _navigation.SetEnabled(false, 0);
        _navigation.SetEnabled(false, 1);
        _navigation.Activated += (_, _) =>
        {
            if (_navigation.SelectedSegment == 0) BackRequested?.Invoke(this, EventArgs.Empty);
            else if (_navigation.SelectedSegment == 1) ForwardRequested?.Invoke(this, EventArgs.Empty);
        };

        // Root: backdrop, then the sidebar veil and the page zone side by side.
        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(new ThemeBackdropView(), root);

        var veil = new NSVisualEffectView
        {
            BlendingMode = NSVisualEffectBlendingMode.WithinWindow,
            Material = NSVisualEffectMaterial.Sidebar,
            State = NSVisualEffectState.Active,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var tint = new WinoSurfaceView { Fill = WinoSettingsStyle.PaneVeil };
        var sidebarPane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(veil, sidebarPane);
        WinoLayout.Fill(tint, sidebarPane);
        var sidebarView = _sidebar.View;
        WinoLayout.Fill(sidebarView, sidebarPane);
        root.AddSubview(sidebarPane);
        // With a wallpaper theme the sidebar is a light veil over the backdrop (the Windows pane);
        // without one it is the regular macOS sidebar material.
        EventHandler updateVeil = (_, _) => veil.Hidden = WinoStyle.HasBackdrop;
        updateVeil(null, EventArgs.Empty);
        WinoStyle.BackdropChanged += updateVeil;
        window.WillClose += (_, _) => WinoStyle.BackdropChanged -= updateVeil;

        ((WinoSurfaceView)_zone.ContentView).CornerRadius = 7;
        root.AddSubview(_zone);

        _crumb = WinoStyle.Label(string.Empty, WinoSettingsStyle.PageTitle, WinoStyle.SecondaryText);
        _crumb.Hidden = true;
        _title = WinoStyle.Label(string.Empty, WinoSettingsStyle.PageTitle, WinoStyle.PrimaryText);
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var titleRow = WinoLayout.HStack(8, _crumb, _title);
        titleRow.Alignment = NSLayoutAttribute.FirstBaseline;
        _description = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.SecondaryText, 0);
        var header = WinoLayout.VStack(4, titleRow, _description);
        header.Alignment = NSLayoutAttribute.Leading;
        header.EdgeInsets = new NSEdgeInsets(22, 32, 6, 32);
        titleRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -64).Active = true;
        _description.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -64).Active = true;

        var content = _zone.ContentView;
        content.AddSubview(header);
        content.AddSubview(_pageHost);
        NSLayoutConstraint.ActivateConstraints(
        [
            sidebarPane.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            sidebarPane.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            sidebarPane.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            sidebarPane.WidthAnchor.ConstraintEqualTo((nfloat)SidebarWidth),
            _zone.LeadingAnchor.ConstraintEqualTo(sidebarPane.TrailingAnchor, 4),
            _zone.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -7),
            _zone.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            _zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -7),
            header.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _pageHost.TopAnchor.ConstraintEqualTo(header.BottomAnchor),
            _pageHost.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _pageHost.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _pageHost.BottomAnchor.ConstraintEqualTo(content.BottomAnchor)
        ]);

        // Auto Layout may resize a window (stay-put priority 500); the minimum size is required here.
        root.WidthAnchor.ConstraintGreaterThanOrEqualTo(760).Active = true;
        root.HeightAnchor.ConstraintGreaterThanOrEqualTo(480).Active = true;
        var controller = new NSViewController { View = root };
        controller.AddChildViewController(_sidebar);
        ContentViewController = controller;
        if (!window.SetFrameUsingName(AutosaveName) || window.Frame.Width < 760 || window.Frame.Height < 480)
        {
            window.SetContentSize(new CGSize(900, 620));
            window.Center();
        }
        window.FrameAutosaveName = AutosaveName;

        var toolbar = new NSToolbar(ToolbarIdentifier)
        {
            DisplayMode = NSToolbarDisplayMode.Icon,
            AllowsUserCustomization = false,
        };
        _toolbarDelegate = new ToolbarDelegate(this);
        toolbar.Delegate = _toolbarDelegate;
        window.Toolbar = toolbar;
        _sidebar.PageSelected += (_, page) => SidebarPageSelected?.Invoke(this, page);
        window.WillClose += (_, _) => Closing?.Invoke(this, EventArgs.Empty);
        window.DidBecomeKey += (_, _) => ActiveChanged?.Invoke(this, true);
        window.DidResignKey += (_, _) => ActiveChanged?.Invoke(this, window.IsMainWindow);
    }

    public event EventHandler? BackRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler<WinoPage>? SidebarPageSelected;
    public event EventHandler? Closing;
    public event EventHandler<bool>? ActiveChanged;

    public SettingsSidebarViewController Sidebar => _sidebar;

    public void Present()
    {
        var window = Window!;
        NSApplication.SharedApplication.Activate();
        window.MakeKeyAndOrderFront(this);
    }

    /// <summary>Places a page controller in the zone, replacing the previous one, under the breadcrumb title.</summary>
    public void SetPage(NSViewController controller, string title, WinoPage page, WinoPage rootPage, string? description = null)
    {
        if (_page is not null)
        {
            _page.View.RemoveFromSuperview();
            _page.RemoveFromParentViewController();
        }
        _page = controller;
        ContentViewController!.AddChildViewController(controller);
        var view = controller.View;
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _pageHost.AddSubview(view);
        WinoLayout.Fill(view, _pageHost);
        SetTitle(title, page, rootPage, description);
        _sidebar.Select(rootPage);
    }

    public void ClearPage()
    {
        if (_page is null) return;
        _page.View.RemoveFromSuperview();
        _page.RemoveFromParentViewController();
        _page = null;
    }

    /// <summary>Window title, plus the in-zone breadcrumb: "Root ›" in secondary colour before a sub-page title.</summary>
    public void SetTitle(string title, WinoPage page, WinoPage rootPage, string? description = null)
    {
        var text = string.IsNullOrWhiteSpace(title) ? Translator.MenuSettings : title;
        Window!.Title = text;
        _title.StringValue = text;
        var nested = page != rootPage;
        _crumb.Hidden = !nested;
        _crumb.StringValue = nested ? SettingsPageCatalog.Title(rootPage) + "  ›" : string.Empty;
        description ??= SettingsPageCatalog.Description(page);
        _description.StringValue = description;
        _description.Hidden = string.IsNullOrWhiteSpace(description);
    }

    public void SetNavigationState(bool canGoBack, bool canGoForward)
    {
        _navigation.SetEnabled(canGoBack, 0);
        _navigation.SetEnabled(canGoForward, 1);
    }

    private static NSWindow CreateWindow()
    {
        var window = new SettingsWindow(new CGRect(0, 0, 900, 620),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered, false)
        {
            ReleasedWhenClosed = false,
            ToolbarStyle = NSWindowToolbarStyle.Unified,
            TitleVisibility = NSWindowTitleVisibility.Visible,
            TitlebarAppearsTransparent = true,
            ContentMinSize = new CGSize(760, 480),
            Title = Translator.MenuSettings,
            TabbingMode = NSWindowTabbingMode.Disallowed
        };
        return window;
    }

    private sealed class ToolbarDelegate(SettingsWindowController owner) : NSToolbarDelegate
    {
        private static string[] Identifiers => [NavigationItemIdentifier];

        public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Identifiers;
        public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Identifiers;

        public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
        {
            if (itemIdentifier != NavigationItemIdentifier) return new NSToolbarItem(itemIdentifier);
            return new NSToolbarItem(itemIdentifier)
            {
                View = owner._navigation,
                Label = Translator.Buttons_Back,
                PaletteLabel = Translator.Buttons_Back,
                Navigational = true
            };
        }
    }

    /// <summary>Escape and Cmd+W close the window, as in System Settings.</summary>
    private sealed class SettingsWindow(CGRect contentRect, NSWindowStyle style, NSBackingStore backing, bool defer)
        : NSWindow(contentRect, style, backing, defer)
    {
        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => PerformClose(this);

        public override void KeyDown(NSEvent theEvent)
        {
            var command = theEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask);
            if (command && string.Equals(theEvent.CharactersIgnoringModifiers, "w", StringComparison.OrdinalIgnoreCase))
            {
                PerformClose(this);
                return;
            }
            if (theEvent.KeyCode == 53)
            {
                PerformClose(this);
                return;
            }
            base.KeyDown(theEvent);
        }

        public override bool PerformKeyEquivalent(NSEvent theEvent)
        {
            var flags = theEvent.ModifierFlags & NSEventModifierMask.DeviceIndependentModifierFlagsMask;
            if (flags == NSEventModifierMask.CommandKeyMask && string.Equals(theEvent.CharactersIgnoringModifiers, "w", StringComparison.OrdinalIgnoreCase))
            {
                PerformClose(this);
                return true;
            }
            return base.PerformKeyEquivalent(theEvent);
        }
    }
}
